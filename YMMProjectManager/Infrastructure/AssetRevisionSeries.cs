using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace YMMProjectManager.Infrastructure;

/// <summary>
/// A create-only revision series in an explicitly selected, dedicated directory.
/// All cooperating writers must use this API and the same path. Non-cooperating writers and
/// filesystem aliases are not excluded between the version check and publication.
/// </summary>
public static class AssetRevisionSeries
{
    public sealed record Head(Guid ProjectId, Guid RevisionId, string Sha256);
    public sealed record Revision(Head Receipt, AssetManifest Manifest);
    public sealed record Inspection(Head Root, Head Head, IReadOnlyList<Revision> Revisions);
    public sealed record Selection(Head Root, Head Head, Revision Selected);
    private const string Prefix = "revision-";
    private const string LockFileName = ".asset-revisions.lock";
    private const int MaxManifestBytes = 4 * 1024 * 1024;
    private const int MaxHistoryBytes = 64 * 1024 * 1024;
    private const int MaxRevisions = 4096;

    /// <summary>Copies an explicitly selected legacy history to a new directory, preserving every source byte.</summary>
    public static async Task<Inspection> ImportLegacyAsync(string directory, Guid projectId, IEnumerable<string> files, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        token.ThrowIfCancellationRequested();
        if (projectId == Guid.Empty) throw new ArgumentException("The project identity is required.", nameof(projectId));
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var parent = ValidateDirectory(Path.GetDirectoryName(destination) ?? throw new ArgumentException("A new series directory is required.", nameof(directory)));
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("The import destination already exists.");

        var history = new Dictionary<Guid, Node>();
        var contents = new Dictionary<Guid, byte[]>();
        var remainingBytes = MaxHistoryBytes;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (history.Count >= MaxRevisions) throw new InvalidDataException("The revision history exceeds the entry limit.");
            var full = Path.GetFullPath(file);
            ValidateDirectory(Path.GetDirectoryName(full)!);
            var bytes = await ReadRegularFileAsync(full, remainingBytes, token).ConfigureAwait(false);
            remainingBytes -= bytes.Length;
            var node = ParseNode(bytes);
            if (node.Manifest.ProjectId != projectId || !history.TryAdd(node.Manifest.RevisionId, node))
                throw new InvalidDataException("The history has a foreign project or duplicate revision ID.");
            contents.Add(node.Manifest.RevisionId, bytes);
        }

        var inspection = InspectNodes(history);
        token.ThrowIfCancellationRequested();
        var staging = Path.Combine(parent, $".asset-import-{Guid.NewGuid():N}");
        if (!CreateDirectory(ToExtendedPath(staging), IntPtr.Zero))
            throw new IOException("The import staging directory could not be reserved.", new Win32Exception(Marshal.GetLastWin32Error()));
        var ownedFiles = new List<string>();
        var published = false;
        try
        {
            foreach (var (revisionId, bytes) in contents)
            {
                token.ThrowIfCancellationRequested();
                var path = RevisionPath(staging, revisionId);
                await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                ownedFiles.Add(path);
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            token.ThrowIfCancellationRequested();
            ValidateDirectory(staging);
            Directory.Move(staging, destination);
            published = true;
            return inspection;
        }
        finally
        {
            if (!published)
            {
                // Only names created by this call are considered for cleanup.
                try
                {
                    ValidateDirectory(staging);
                    foreach (var file in ownedFiles) File.Delete(file);
                    Directory.Delete(staging, recursive: false);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);

    private static string ToExtendedPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    /// <summary>Initializes an empty series from a root manifest. Existing history is never replaced.</summary>
    public static async Task<Head> InitializeAsync(string directory, AssetManifest root, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(root);
        if (root.ParentRevisionId is not null) throw new InvalidDataException("A series must start with an actual root revision.");
        root = Snapshot(root);
        var full = ValidateDirectory(directory);
        await using var gate = AcquireLock(full);
        if ((await ReadNodesAsync(full, token).ConfigureAwait(false)).Count != 0)
            throw new InvalidDataException("The revision series already contains history.");
        var destination = RevisionPath(full, root.RevisionId);
        var head = MakeHead(root);
        await AssetManifestStore.SaveNewAsync(destination, root, token).ConfigureAwait(false);
        return head;
    }

    /// <summary>Reads the only valid head; missing, corrupt, disconnected or divergent history is rejected.</summary>
    public static async Task<Head> ReadHeadAsync(string directory, CancellationToken token = default)
        => (await InspectAsync(directory, token).ConfigureAwait(false)).Head;

    /// <summary>Returns the immutable revisions of an unambiguous series in parent order.</summary>
    public static async Task<Inspection> InspectAsync(string directory, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var full = ValidateDirectory(directory);
        await using var gate = AcquireLock(full);
        return await InspectCoreAsync(full, token).ConfigureAwait(false);
    }

    /// <summary>Resolves an explicitly selected revision and verifies that the series and revision still match.</summary>
    public static async Task<Selection> ResolveAsync(string directory, Guid projectId, Guid rootRevisionId,
        string rootSha256, Guid selectedRevisionId, string selectedSha256, CancellationToken token = default)
    {
        var inspection = await InspectAsync(directory, token).ConfigureAwait(false);
        if (inspection.Root.ProjectId != projectId || inspection.Root.RevisionId != rootRevisionId ||
            !string.Equals(inspection.Root.Sha256, rootSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The associated metadata series was replaced or belongs to a different project.");

        var selected = inspection.Revisions.SingleOrDefault(revision => revision.Receipt.RevisionId == selectedRevisionId);
        if (selected is null || !string.Equals(selected.Receipt.Sha256, selectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected metadata revision is missing or has changed.");
        return new Selection(inspection.Root, inspection.Head, selected);
    }

    /// <summary>Publishes a new revision only if the expected head identity and bytes still match.</summary>
    public static Task<Head> AppendAsync(string directory, Head expected, AssetManifest next, CancellationToken token = default)
        => AppendCoreAsync(directory, null, expected, next, acceptCommittedRevision: false, token);

    public static Task<Head> AppendAsync(string directory, Head expectedRoot, Head expected, AssetManifest next, CancellationToken token = default)
        => AppendCoreAsync(directory, expectedRoot, expected, next, acceptCommittedRevision: false, token);

    /// <summary>Retries a fixed revision without creating another if the exact expected revision was already published.</summary>
    public static Task<Head> AppendOrVerifyAsync(string directory, Head expected, AssetManifest next, CancellationToken token = default)
        => AppendCoreAsync(directory, null, expected, next, acceptCommittedRevision: true, token);

    internal static Task<Head> AppendOrVerifyAsync(string directory, Head expectedRoot, Head expected, AssetManifest next, CancellationToken token)
        => AppendCoreAsync(directory, expectedRoot, expected, next, acceptCommittedRevision: true, token);

    private static async Task<Head> AppendCoreAsync(string directory, Head? expectedRoot, Head expected, AssetManifest next,
        bool acceptCommittedRevision, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(expected);
        token.ThrowIfCancellationRequested();
        next = Snapshot(next);
        if (next.ProjectId != expected.ProjectId || next.ParentRevisionId != expected.RevisionId)
            throw new InvalidDataException("The new revision does not reference the expected project and parent.");
        var full = ValidateDirectory(directory);
        await using var gate = AcquireLock(full);
        var nodes = await ReadNodesAsync(full, token).ConfigureAwait(false);
        var inspection = InspectNodes(nodes);
        if (expectedRoot is not null && inspection.Root != expectedRoot)
            throw new InvalidDataException("The associated metadata series changed; reload before updating.");
        var head = MakeHead(next);
        if (acceptCommittedRevision && inspection.Revisions.FirstOrDefault(r => r.Receipt.RevisionId == next.RevisionId) is { } committed)
        {
            if (committed.Receipt != head || !inspection.Revisions.Any(r => r.Receipt == expected))
                throw new InvalidDataException("An existing revision or its parent does not match the pending operation.");
            return committed.Receipt;
        }

        if (inspection.Head != expected) throw new InvalidDataException("The revision series changed; reload before updating.");
        if (nodes.Count >= MaxRevisions || nodes.Values.Sum(node => node.ByteCount) + SerializeRevision(next).Length > MaxHistoryBytes)
            throw new InvalidDataException("The revision history exceeds the storage limit.");
        await AssetManifestStore.SaveNewAsync(RevisionPath(full, next.RevisionId), next, token).ConfigureAwait(false);
        return head;
    }

    private static string ValidateDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var full = Path.GetFullPath(directory);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Revision directory cannot traverse a reparse point.");
        return full;
    }

    private static FileStream AcquireLock(string directory)
    {
        var path = Path.Combine(directory, LockFileName);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Revision lock cannot be a reparse point.");
        // The sidecar is retained: deleting it could split cooperating writers across lock files.
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static string RevisionPath(string directory, Guid revision) => Path.Combine(directory, $"{Prefix}{revision:N}.json");

    private static AssetManifest Snapshot(AssetManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return AssetManifestStore.Parse(JsonSerializer.SerializeToUtf8Bytes(manifest));
    }

    private static byte[] SerializeRevision(AssetManifest manifest)
        => JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });

    private static Head MakeHead(AssetManifest manifest) => new(manifest.ProjectId, manifest.RevisionId,
        Convert.ToHexString(SHA256.HashData(SerializeRevision(manifest))));

    private sealed record Node(AssetManifest Manifest, string Hash, int ByteCount);

    private static async Task<Dictionary<Guid, Node>> ReadNodesAsync(string directory, CancellationToken token)
    {
        var nodes = new Dictionary<Guid, Node>();
        var remainingBytes = MaxHistoryBytes;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory, Prefix + "*", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            if (nodes.Count >= MaxRevisions) throw new InvalidDataException("The revision history exceeds the entry limit.");
            var name = Path.GetFileName(path);
            if (name.Length != Prefix.Length + 32 + 5 || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(name.Substring(Prefix.Length, 32), "N", out var nameId))
                throw new InvalidDataException("An unrecognized revision entry exists; history was not modified.");
            var bytes = await ReadRegularFileAsync(path, remainingBytes, token).ConfigureAwait(false);
            remainingBytes -= bytes.Length;
            var node = ParseNode(bytes);
            if (node.Manifest.RevisionId != nameId || !nodes.TryAdd(nameId, node))
                throw new InvalidDataException("Revision identifier collision or filename mismatch.");
        }
        return nodes;
    }

    private static async Task<byte[]> ReadRegularFileAsync(string path, int remainingBytes, CancellationToken token)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("A revision entry is not a regular file.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (stream.Length > MaxManifestBytes || stream.Length > remainingBytes)
            throw new InvalidDataException("The revision or its history exceeds the size limit.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    private static Node ParseNode(byte[] bytes)
    {
        var json = bytes.AsMemory();
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) json = json[3..];
        using var doc = JsonDocument.Parse(json);
        if (HasDuplicateProperties(doc.RootElement)) throw new InvalidDataException("A revision contains duplicate properties.");
        return new Node(AssetManifestStore.Parse(bytes), Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (HasDuplicateProperties(item)) return true;
        return false;
    }

    private static async Task<Inspection> InspectCoreAsync(string directory, CancellationToken token)
        => InspectNodes(await ReadNodesAsync(directory, token).ConfigureAwait(false));

    private static Inspection InspectNodes(Dictionary<Guid, Node> nodes)
    {
        if (nodes.Count == 0) throw new InvalidDataException("The revision series is empty.");
        var projectId = nodes.Values.First().Manifest.ProjectId;
        if (nodes.Values.Any(n => n.Manifest.ProjectId != projectId)) throw new InvalidDataException("The revision series contains different projects.");
        var parents = new HashSet<Guid>();
        foreach (var node in nodes.Values)
            if (node.Manifest.ParentRevisionId is Guid parent)
            {
                if (!nodes.ContainsKey(parent)) throw new InvalidDataException("A parent revision is missing.");
                parents.Add(parent);
            }
        var heads = nodes.Keys.Where(id => !parents.Contains(id)).ToArray();
        if (heads.Length != 1) throw new InvalidDataException("Revision history is divergent or cyclic; select or repair it explicitly.");
        var visited = new HashSet<Guid>();
        var ordered = new List<Revision>(nodes.Count);
        Guid? cursor = heads[0];
        while (cursor is Guid id)
        {
            if (!visited.Add(id)) throw new InvalidDataException("Revision history contains a cycle.");
            var node = nodes[id];
            ordered.Add(new Revision(new Head(projectId, id, node.Hash), node.Manifest));
            cursor = node.Manifest.ParentRevisionId;
        }
        if (visited.Count != nodes.Count) throw new InvalidDataException("Revision history is disconnected.");
        ordered.Reverse();
        return new Inspection(ordered[0].Receipt, ordered[^1].Receipt, ordered.AsReadOnly());
    }
}
