using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace YMMProjectManager.Infrastructure;

/// <summary>
/// A create-only revision series in an explicitly selected, dedicated directory.
/// All cooperating writers must use this API and the same path. Appends hold the revision files read
/// during validation until publication, while other writers can still add independent directory entries.
/// </summary>
public static class AssetRevisionSeries
{
    public sealed record Head(Guid ProjectId, Guid RevisionId, string Sha256);
    public sealed record Revision(Head Receipt, AssetManifest Manifest);
    public sealed record Inspection(Head Root, Head Head, IReadOnlyList<Revision> Revisions);
    public sealed record Selection(Head Root, Head Head, Revision Selected);
    public sealed record GraphInspection(Head Root, IReadOnlyList<Head> Heads, IReadOnlyList<Revision> Revisions);
    public sealed record BranchSelection(GraphInspection Graph, Revision Selected);
    private const string Prefix = "revision-";
    private const string LockFileName = ".asset-revisions.lock";
    private const int MaxManifestBytes = 4 * 1024 * 1024;
    private const int MaxHistoryBytes = 64 * 1024 * 1024;
    private const int MaxRevisions = 4096;

    /// <summary>Copies an explicitly selected legacy history to a new directory, preserving every source byte.</summary>
    public static async Task<Inspection> ImportLegacyAsync(string directory, Guid projectId, IEnumerable<string> files, CancellationToken token = default)
    {
        var graph = await ImportLegacyCoreAsync(directory, projectId, files, requireSingleHead: true, token).ConfigureAwait(false);
        return new Inspection(graph.Root, graph.Heads[0], graph.Revisions);
    }

    /// <summary>Imports every explicitly selected revision while retaining all branches.</summary>
    public static Task<GraphInspection> ImportLegacyGraphAsync(string directory, Guid projectId, IEnumerable<string> files,
        CancellationToken token = default) => ImportLegacyCoreAsync(directory, projectId, files, requireSingleHead: false, token);

    internal const string RetainedImportPath = "AssetMetadataRetainedImportPath";

    internal static Task<GraphInspection> ImportLegacyGraphAsync(string directory, Guid projectId, IEnumerable<string> files,
        CancellationToken token, Action<string> beforePublication)
        => ImportLegacyCoreAsync(directory, projectId, files, requireSingleHead: false, token, beforePublication);

    private static async Task<GraphInspection> ImportLegacyCoreAsync(string directory, Guid projectId, IEnumerable<string> files,
        bool requireSingleHead, CancellationToken token, Action<string>? beforePublication = null)
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

        var graph = InspectGraph(history);
        if (requireSingleHead) RequireSingleHead(graph);
        token.ThrowIfCancellationRequested();
        using var staging = OwnedStagingDirectory.Reserve(parent, ".asset-import-");
        try
        {
            foreach (var (revisionId, bytes) in contents)
            {
                token.ThrowIfCancellationRequested();
                var path = RevisionPath(staging.Path, revisionId);
                await using var stream = OwnedFilePublication.CreateOwnedOutputFile(path);
                var complete = false;
                try
                {
                    await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                    await stream.FlushAsync(token).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                    complete = true;
                }
                finally
                {
                    if (!complete) OwnedFilePublication.TryDeleteOpenedPath(stream.SafeFileHandle);
                }
            }

            beforePublication?.Invoke(staging.Path);
            token.ThrowIfCancellationRequested();
            var staged = await ReadNodesAsync(staging.Path, token).ConfigureAwait(false);
            if (Directory.EnumerateFileSystemEntries(staging.Path).Take(history.Count + 1).Count() != history.Count ||
                staged.Count != history.Count || staged.Any(pair => !history.TryGetValue(pair.Key, out var expected) ||
                    pair.Value.Hash != expected.Hash))
                throw new InvalidDataException("The imported history differs from the explicitly selected revisions.");
            staging.Publish(destination, token);
            return graph;
        }
        catch (Exception ex)
        {
            // The temporary directory can contain externally changed files. Leave them for diagnosis.
            try
            {
                if (staging.CheckRecoveryPath() is { } retained) ex.Data[RetainedImportPath] = retained;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Initializes an empty series from a root manifest. Existing history is never replaced.</summary>
    public static async Task<Head> InitializeAsync(string directory, AssetManifest root, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(root);
        if (root.ParentRevisionId is not null) throw new InvalidDataException("A series must start with an actual root revision.");
        root = Snapshot(root);
        var full = ValidateDirectory(directory);
        using var fence = OwnedFilePublication.DirectoryFence.Open(full);
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
        using var fence = OwnedFilePublication.DirectoryFence.Open(full);
        await using var gate = AcquireLock(full);
        return await InspectCoreAsync(full, token).ConfigureAwait(false);
    }

    /// <summary>Reads a complete, validated history without choosing between multiple tips.</summary>
    public static async Task<GraphInspection> InspectGraphAsync(string directory, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var full = ValidateDirectory(directory);
        using var fence = OwnedFilePublication.DirectoryFence.Open(full);
        await using var gate = AcquireLock(full);
        return InspectGraph(await ReadNodesAsync(full, token).ConfigureAwait(false));
    }

    /// <summary>Resolves an explicitly selected revision and verifies that the series and revision still match.</summary>
    public static async Task<Selection> ResolveAsync(string directory, Guid projectId, Guid rootRevisionId,
        string rootSha256, Guid selectedRevisionId, string selectedSha256, CancellationToken token = default)
    {
        var selected = await ResolveBranchAsync(directory, projectId, rootRevisionId, rootSha256,
            selectedRevisionId, selectedSha256, token).ConfigureAwait(false);
        return new Selection(selected.Graph.Root, RequireSingleHead(selected.Graph), selected.Selected);
    }

    public static async Task<BranchSelection> ResolveBranchAsync(string directory, Guid projectId, Guid rootRevisionId,
        string rootSha256, Guid selectedRevisionId, string selectedSha256, CancellationToken token = default)
    {
        var graph = await InspectGraphAsync(directory, token).ConfigureAwait(false);
        if (graph.Root.ProjectId != projectId || graph.Root.RevisionId != rootRevisionId ||
            !string.Equals(graph.Root.Sha256, rootSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The associated metadata series was replaced or belongs to a different project.");

        var selected = graph.Revisions.SingleOrDefault(revision => revision.Receipt.RevisionId == selectedRevisionId);
        if (selected is null || !string.Equals(selected.Receipt.Sha256, selectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected metadata revision is missing or has changed.");
        return new BranchSelection(graph, selected);
    }

    /// <summary>Publishes a new revision only if the expected head identity and bytes still match.</summary>
    public static Task<Head> AppendAsync(string directory, Head expected, AssetManifest next, CancellationToken token = default)
        => AppendCoreAsync(directory, null, null, expected, next, acceptCommittedRevision: false, token);

    public static Task<Head> AppendAsync(string directory, Head expectedRoot, Head expected, AssetManifest next, CancellationToken token = default)
        => AppendCoreAsync(directory, expectedRoot, null, expected, next, acceptCommittedRevision: false, token);

    /// <summary>Extends the selected branch tip while keeping every other branch intact.</summary>
    public static Task<Head> AppendToBranchAsync(string directory, Head expectedRoot, IReadOnlyList<Head> observedTips, Head expectedTip,
        AssetManifest next, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(expectedRoot);
        ArgumentNullException.ThrowIfNull(observedTips);
        return AppendCoreAsync(directory, expectedRoot, observedTips.ToArray(), expectedTip, next, acceptCommittedRevision: false, token);
    }

    /// <summary>Retries a fixed revision without creating another if the exact expected revision was already published.</summary>
    public static Task<Head> AppendOrVerifyAsync(string directory, Head expected, AssetManifest next, CancellationToken token = default)
        => AppendCoreAsync(directory, null, null, expected, next, acceptCommittedRevision: true, token);

    internal static Task<Head> AppendOrVerifyAsync(string directory, Head expectedRoot, Head expected, AssetManifest next, CancellationToken token)
        => AppendCoreAsync(directory, expectedRoot, null, expected, next, acceptCommittedRevision: true, token);

    internal static Task<Head> AppendToBranchOrVerifyAsync(string directory, Head expectedRoot, IReadOnlyList<Head> observedTips,
        Head expectedTip, AssetManifest next, CancellationToken token, Action? publishPreparedOutput = null)
        => AppendCoreAsync(directory, expectedRoot, observedTips.ToArray(), expectedTip, next, acceptCommittedRevision: true, token, publishPreparedOutput);

    internal static bool SameTips(IReadOnlyList<Head> current, IReadOnlyList<Head> expected)
        => current.Count == expected.Count && current.ToHashSet().SetEquals(expected);

    private static async Task<Head> AppendCoreAsync(string directory, Head? expectedRoot, Head[]? observedTips, Head expected, AssetManifest next,
        bool acceptCommittedRevision, CancellationToken token, Action? publishPreparedOutput = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        token.ThrowIfCancellationRequested();
        next = Snapshot(next);
        if (next.ProjectId != expected.ProjectId || next.ParentRevisionId != expected.RevisionId)
            throw new InvalidDataException("The new revision does not reference the expected project and parent.");
        var full = ValidateDirectory(directory);
        using var fence = OwnedFilePublication.DirectoryFence.Open(full);
        await using var gate = AcquireLock(full);
        using var history = new RevisionReadPins();
        var nodes = await ReadNodesAsync(full, token, history).ConfigureAwait(false);
        var graph = InspectGraph(nodes);
        if (expectedRoot is not null && graph.Root != expectedRoot)
            throw new InvalidDataException("The associated metadata series changed; reload before updating.");
        var head = MakeHead(next);
        if (acceptCommittedRevision && graph.Revisions.FirstOrDefault(r => r.Receipt.RevisionId == next.RevisionId) is { } committed)
        {
            if (committed.Receipt != head || !graph.Revisions.Any(r => r.Receipt == expected))
                throw new InvalidDataException("An existing revision or its parent does not match the pending operation.");
            token.ThrowIfCancellationRequested();
            publishPreparedOutput?.Invoke();
            return committed.Receipt;
        }

        if (observedTips is null) RequireSingleHead(graph);
        else if (!SameTips(graph.Heads, observedTips))
            throw new InvalidDataException("The revision branches changed; inspect them before updating.");
        if (!graph.Heads.Contains(expected)) throw new InvalidDataException("The selected revision is no longer a branch tip.");
        if (nodes.Count >= MaxRevisions || nodes.Values.Sum(node => node.ByteCount) + SerializeRevision(next).Length > MaxHistoryBytes)
            throw new InvalidDataException("The revision history exceeds the storage limit.");
        token.ThrowIfCancellationRequested();
        // A refused or busy series must leave a prepared output unpublished. If writing the revision fails,
        // the output retains its durable proof and can be verified by a later recovery attempt.
        publishPreparedOutput?.Invoke();
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

    private sealed class RevisionReadPins : IDisposable
    {
        private readonly List<FileStream> streams = [];

        public FileStream Open(string path)
        {
            // The bytes are already buffered by ReadRevisionBytesAsync. Keep only the file handle.
            var stream = OpenRegularRevision(path, bufferSize: 1);
            streams.Add(stream);
            return stream;
        }

        public void Dispose()
        {
            foreach (var stream in streams) stream.Dispose();
        }
    }

    private static async Task<Dictionary<Guid, Node>> ReadNodesAsync(string directory, CancellationToken token,
        RevisionReadPins? history = null)
    {
        var nodes = new Dictionary<Guid, Node>();
        var remainingBytes = MaxHistoryBytes;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory, Prefix + "*", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            // Older writers staged revisions in the committed namespace. Leave those files available
            // for diagnosis after interruption, and never inspect them as published revisions.
            if (IsLegacyRevisionStage(name)) continue;
            if (nodes.Count >= MaxRevisions) throw new InvalidDataException("The revision history exceeds the entry limit.");
            if (name.Length != Prefix.Length + 32 + 5 || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(name.Substring(Prefix.Length, 32), "N", out var nameId))
                throw new InvalidDataException("An unrecognized revision entry exists; history was not modified.");
            var bytes = history is null
                ? await ReadRegularFileAsync(path, remainingBytes, token).ConfigureAwait(false)
                : await ReadRevisionBytesAsync(history.Open(path), remainingBytes, token).ConfigureAwait(false);
            remainingBytes -= bytes.Length;
            var node = ParseNode(bytes);
            if (node.Manifest.RevisionId != nameId || !nodes.TryAdd(nameId, node))
                throw new InvalidDataException("Revision identifier collision or filename mismatch.");
        }
        return nodes;
    }

    private static bool IsLegacyRevisionStage(string name)
    {
        const string marker = ".json.tmp-";
        const int idLength = 32;
        var markerOffset = Prefix.Length + idLength;
        var tempIdOffset = markerOffset + marker.Length;
        return name.Length == tempIdOffset + idLength &&
            name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) &&
            name.AsSpan(markerOffset, marker.Length).Equals(marker, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(name.AsSpan(Prefix.Length, idLength), "N", out _) &&
            Guid.TryParseExact(name.AsSpan(tempIdOffset), "N", out _);
    }

    private static async Task<byte[]> ReadRegularFileAsync(string path, int remainingBytes, CancellationToken token)
    {
        await using var stream = OpenRegularRevision(path);
        return await ReadRevisionBytesAsync(stream, remainingBytes, token).ConfigureAwait(false);
    }

    private static FileStream OpenRegularRevision(string path, int bufferSize = 81920)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("A revision entry is not a regular file.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, true);
    }

    private static async Task<byte[]> ReadRevisionBytesAsync(FileStream stream, int remainingBytes, CancellationToken token)
    {
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
        var graph = InspectGraph(nodes);
        return new Inspection(graph.Root, RequireSingleHead(graph), graph.Revisions);
    }

    private static Head RequireSingleHead(GraphInspection graph)
        => graph.Heads.Count == 1 ? graph.Heads[0] :
            throw new InvalidDataException("Revision history is divergent. Select a branch explicitly.");

    private static GraphInspection InspectGraph(Dictionary<Guid, Node> nodes)
    {
        if (nodes.Count == 0) throw new InvalidDataException("The revision series is empty.");
        var projectId = nodes.Values.First().Manifest.ProjectId;
        if (nodes.Values.Any(n => n.Manifest.ProjectId != projectId)) throw new InvalidDataException("The revision series contains different projects.");
        var children = new Dictionary<Guid, List<Guid>>();
        Guid? root = null;
        foreach (var node in nodes.Values)
        {
            if (node.Manifest.ParentRevisionId is Guid parent)
            {
                if (!nodes.ContainsKey(parent)) throw new InvalidDataException("A parent revision is missing.");
                if (!children.TryGetValue(parent, out var siblings))
                    children.Add(parent, siblings = []);
                siblings.Add(node.Manifest.RevisionId);
            }
            else if (root is null) root = node.Manifest.RevisionId;
            else throw new InvalidDataException("Revision history has several roots.");
        }
        if (root is null) throw new InvalidDataException("Revision history has no root.");
        var heads = new List<Head>();
        var ordered = new List<Revision>(nodes.Count);
        var pending = new Queue<Guid>();
        pending.Enqueue(root.Value);
        while (pending.TryDequeue(out var id))
        {
            var node = nodes[id];
            var receipt = new Head(projectId, id, node.Hash);
            ordered.Add(new Revision(receipt, node.Manifest));
            if (children.TryGetValue(id, out var next))
            {
                next.Sort();
                foreach (var descendant in next) pending.Enqueue(descendant);
            }
            else heads.Add(receipt);
        }
        if (ordered.Count != nodes.Count) throw new InvalidDataException("Revision history is disconnected or cyclic.");
        return new GraphInspection(ordered[0].Receipt, heads.AsReadOnly(), ordered.AsReadOnly());
    }
}
