using System.IO;
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
    private const string Prefix = "revision-";
    private const string LockFileName = ".asset-revisions.lock";
    private const int MaxManifestBytes = 4 * 1024 * 1024;

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
    {
        token.ThrowIfCancellationRequested();
        var full = ValidateDirectory(directory);
        await using var gate = AcquireLock(full);
        return await ReadHeadCoreAsync(full, token).ConfigureAwait(false);
    }

    /// <summary>Publishes a new revision only if the expected head identity and bytes still match.</summary>
    public static async Task<Head> AppendAsync(string directory, Head expected, AssetManifest next, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        token.ThrowIfCancellationRequested();
        next = Snapshot(next);
        if (next.ProjectId != expected.ProjectId || next.ParentRevisionId != expected.RevisionId)
            throw new InvalidDataException("The new revision does not reference the expected project and parent.");
        var full = ValidateDirectory(directory);
        await using var gate = AcquireLock(full);
        var current = await ReadHeadCoreAsync(full, token).ConfigureAwait(false);
        if (current != expected) throw new InvalidDataException("The revision series changed; reload before updating.");
        var destination = RevisionPath(full, next.RevisionId);
        var head = MakeHead(next);
        await AssetManifestStore.SaveNewAsync(destination, next, token).ConfigureAwait(false);
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

    private static Head MakeHead(AssetManifest manifest) => new(manifest.ProjectId, manifest.RevisionId,
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true }))));

    private sealed record Node(AssetManifest Manifest, string Hash);

    private static async Task<Dictionary<Guid, Node>> ReadNodesAsync(string directory, CancellationToken token)
    {
        var nodes = new Dictionary<Guid, Node>();
        foreach (var path in Directory.EnumerateFileSystemEntries(directory, Prefix + "*", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (name.Length != Prefix.Length + 32 + 5 || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(name.Substring(Prefix.Length, 32), "N", out var nameId))
                throw new InvalidDataException("An unrecognized revision entry exists; history was not modified.");
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException("A revision entry is not a regular file.");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            if (stream.Length > MaxManifestBytes) throw new InvalidDataException("Revision is too large.");
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            var json = bytes.AsMemory();
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) json = json[3..];
            using (var doc = JsonDocument.Parse(json))
                if (HasDuplicateProperties(doc.RootElement)) throw new InvalidDataException("A revision contains duplicate properties.");
            var manifest = AssetManifestStore.Parse(bytes);
            if (manifest.RevisionId != nameId || !nodes.TryAdd(nameId, new Node(manifest, Convert.ToHexString(SHA256.HashData(bytes)))))
                throw new InvalidDataException("Revision identifier collision or filename mismatch.");
        }
        return nodes;
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

    private static async Task<Head> ReadHeadCoreAsync(string directory, CancellationToken token)
    {
        var nodes = await ReadNodesAsync(directory, token).ConfigureAwait(false);
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
        Guid? cursor = heads[0];
        while (cursor is Guid id)
        {
            if (!visited.Add(id)) throw new InvalidDataException("Revision history contains a cycle.");
            cursor = nodes[id].Manifest.ParentRevisionId;
        }
        if (visited.Count != nodes.Count) throw new InvalidDataException("Revision history is disconnected.");
        return new Head(projectId, heads[0], nodes[heads[0]].Hash);
    }
}
