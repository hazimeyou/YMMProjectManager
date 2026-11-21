using System.Buffers;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YMMProjectManager.Infrastructure.Packaging;

namespace YMMProjectManager.Infrastructure.Git;

public sealed partial class GitRepository
{
    private const int PublicationListingLimit = 4 * 1024 * 1024;
    private const int PublicationObjectLimit = 4 * 1024 * 1024;
    private const long PublicationTotalLimit = 128L * 1024 * 1024;
    private static readonly UTF8Encoding StrictPublicationUtf8 = new(false, true);
    private readonly HashSet<string> publicationAssetNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> publicationAssetHashes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds media known to the selected project before inspecting staged or outgoing objects.</summary>
    internal void SetPublicationAssets(IEnumerable<string> names, IEnumerable<string> hashes)
    {
        foreach (var name in names)
            publicationAssetNames.Add(Path.GetFileName(PackagingDetector.NormalizeFilePathKey(name)));
        foreach (var hash in hashes)
            if (hash.Length == 64 && hash.All(Uri.IsHexDigit))
                publicationAssetHashes.Add(hash);
    }

    internal async Task<GitResult> InspectCurrentCommitAsync(CancellationToken token)
    {
        if (!HasDirectRepository()) return UnsafeRemote("Select a direct Git repository.");
        var (tip, error) = await ReadBranchTipAsync(trusted: true, token, pullSettings: []).ConfigureAwait(false);
        return error ?? new GitResult(0, tip!.ObjectId, "");
    }

    private static GitResult PublicationRefusal(string reason) => new(1, "", reason, GitFailure.MediaPublication);

    private async Task<GitResult?> CheckStagePublicationFileAsync(string path, FileStream file, CancellationToken token)
    {
        if (IsBlockedPublicationPath(path) || file.Length > PublicationObjectLimit)
            return PublicationRefusal("The selected file is an asset, private record or exceeds the media inspection limit.");
        var bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        var reason = InspectPublicationBlob(bytes, new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase));
        return reason is null ? null : PublicationRefusal(reason);
    }

    private bool HasOrdinaryPublicationObjectStore()
    {
        var git = Path.Combine(directory, ".git");
        var objects = Path.Combine(git, "objects");
        if (!HasDirectRepository() || !CanUsePullMetadataFile(git, "objects/info/alternates") ||
            HasGitEntry(Path.Combine(objects, "info", "alternates")) ||
            HasGitEntry(Path.Combine(objects, "info", "http-alternates")) ||
            HasGitEntry(Path.Combine(git, "shallow")) ||
            HasGitEntry(Path.Combine(git, "info", "grafts")))
            return false;
        try
        {
            if (Directory.EnumerateFiles(objects).Any()) return false;
            foreach (var folder in Directory.EnumerateDirectories(objects))
            {
                if (!IsOrdinaryDirectory(folder) || Directory.EnumerateDirectories(folder).Any()) return false;
                if (Directory.EnumerateFiles(folder).Any(path =>
                    !IsRegularFile(path) || path.EndsWith(".promisor", StringComparison.OrdinalIgnoreCase))) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // A Push can introduce old objects even when the tip no longer contains the media.
    // Inspect all reachable history before opening a network connection.
    private async Task<GitResult?> CheckPushPublicationAsync(string commit, CancellationToken token)
    {
        var knownFailure = await ReadKnownPublicationAssetsAsync(token).ConfigureAwait(false);
        if (knownFailure is not null) return knownFailure;
        var complete = await InspectPublicationGitAsync(["rev-parse", "--is-shallow-repository"], token).ConfigureAwait(false);
        if (!complete.Success || complete.StandardOutput.Trim() != "false")
            return PublicationRefusal("Complete local Git history is required to check outgoing media.");
        var paths = await InspectPublicationGitAsync(
            ["log", "--full-history", "--root", "--diff-merges=separate", "--format=", "--raw", "-z",
                "--no-show-signature", "--no-notes", "--no-decorate", "--no-renames", "--no-ext-diff",
                "--no-textconv", "--diff-filter=AMT", commit, "--"], token).ConfigureAwait(false);
        var objects = await InspectPublicationGitAsync(["rev-list", "--objects", "--no-object-names", commit, "--"], token).ConfigureAwait(false);
        if (!paths.Success || !objects.Success)
            return PublicationRefusal("Cannot verify all historical paths and objects.");
        var entries = paths.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var names = new List<string>();
        if (entries.Length % 2 != 0) return PublicationRefusal("Cannot identify historical file names.");
        for (var i = 0; i < entries.Length; i += 2)
        {
            var fields = entries[i].TrimStart('\n').Split(' ');
            if (fields.Length != 5 || !fields[0].StartsWith(':') || fields[1] is not ("100644" or "100755"))
                return PublicationRefusal("Git history contains a link or unsupported Git entry.");
            var path = entries[i + 1];
            if (IsBlockedPublicationPath(path))
                return PublicationRefusal("Git history contains an asset, package or private record path.");
            names.Add(path);
        }
        var ids = objects.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return await CheckPublicationObjectsAsync(ids, names, token).ConfigureAwait(false);
    }

    // A commit is checked against the immutable tree the caller will publish.
    private async Task<GitResult?> CheckPublicationTreeAsync(string tree, CancellationToken token)
    {
        var knownFailure = await ReadKnownPublicationAssetsAsync(token).ConfigureAwait(false);
        if (knownFailure is not null) return knownFailure;
        var listing = await InspectPublicationGitAsync(["ls-tree", "-r", "-z", tree, "--"], token).ConfigureAwait(false);
        if (!listing.Success) return PublicationRefusal("Cannot verify the staged tree.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var record in listing.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = record.IndexOf('\t');
            if (separator < 0) return PublicationRefusal("Cannot identify a staged entry.");
            var fields = record[..separator].Split(' ');
            if (fields.Length != 3 || fields[0] is not ("100644" or "100755") || fields[1] != "blob" || !IsObjectId(fields[2]))
                return PublicationRefusal("The staged tree contains a link or unsupported Git entry.");
            var path = record[(separator + 1)..];
            if (IsBlockedPublicationPath(path))
                return PublicationRefusal("The staged tree contains an asset, package or private record path.");
            names.Add(path);
            ids.Add(fields[2]);
        }
        return await CheckPublicationObjectsAsync(ids, names, token).ConfigureAwait(false);
    }

    private bool IsBlockedPublicationPath(string path) =>
        path.EndsWith(".ymmpx", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".ymme", StringComparison.OrdinalIgnoreCase) ||
        publicationAssetNames.Contains(Path.GetFileName(path)) ||
        IsPrivateRelinkPath(path) ||
        IsAssetPoolPayload(path);

    private static bool IsPrivateRelinkPath(string path) =>
        path.Split('/').Any(component =>
            component.Equals(".ymmpm-lineage.json", StringComparison.OrdinalIgnoreCase) ||
            HasOperationName(component, ".ymmpm-relink-") ||
            HasOperationName(component, ".ymmpm-relink.tmp-") ||
            HasOperationName(component, ".ymmpm-derived-"));

    private static bool HasOperationName(string component, string prefix) =>
        component.Length >= prefix.Length + 32 &&
        component.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParseExact(component.AsSpan(prefix.Length, 32), "N", out _);

    private static bool IsAssetPoolPayload(string path)
    {
        var components = path.Split('/');
        for (var i = 0; i < components.Length - 1; i++)
        {
            var component = components[i];
            if (component.StartsWith(".asset-pool-pending-", StringComparison.OrdinalIgnoreCase)) return true;
            if (component.Length == 87 && component.StartsWith("asset-", StringComparison.OrdinalIgnoreCase) && component[22] == '-' &&
                component.AsSpan(6, 16).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0 &&
                component.AsSpan(23).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0)
                return i != components.Length - 2 || !components[^1].Equals(AssetPool.ManifestFileName, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private Task<GitResult> InspectPublicationGitAsync(string[] arguments, CancellationToken token, string? input = null,
        Func<Stream, CancellationToken, Task>? rawOutput = null) =>
        RunAsync([$"--git-dir={Path.Combine(directory, ".git")}", $"--work-tree={directory}", .. arguments],
            token, trustedProcess: true, ignoreLegacyGrafts: true, pullSettings: [], standardInput: input,
            outputLimit: PublicationListingLimit, rawOutput: rawOutput, noLazyFetch: true);

    private async Task<GitResult?> CheckPublicationObjectsAsync(IReadOnlyCollection<string> ids,
        IReadOnlyCollection<string> paths, CancellationToken token)
    {
        if (ids.Count == 0) return null;
        if (ids.Count > 40_000 || ids.Any(id => !IsObjectId(id)))
            return PublicationRefusal("The Git object list cannot be checked within the inspection bounds.");
        var info = await InspectPublicationGitAsync(["cat-file", "--batch-check"], token,
            string.Join("\n", ids) + "\n").ConfigureAwait(false);
        if (!info.Success) return PublicationRefusal("Cannot read local Git objects.");
        var records = info.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (records.Length != ids.Count) return PublicationRefusal("The Git object list is incomplete.");

        var blobs = new List<(string Id, int Size)>();
        long total = 0;
        foreach (var (entry, expected) in records.Zip(ids))
        {
            var fields = entry.Split(' ');
            if (fields.Length != 3 || fields[0] != expected || fields[1] is not ("commit" or "tree" or "blob") ||
                !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                return PublicationRefusal("The Git object list contains an unreadable entry.");
            if (fields[1] != "blob") continue;
            total += size;
            if (size > PublicationObjectLimit || total > PublicationTotalLimit)
                return PublicationRefusal("Git contains files too large to verify for media publication.");
            blobs.Add((expected, size));
        }
        if (blobs.Count == 0) return null;

        string? refusal = null;
        var historicalAssetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var historicalAssetHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blobHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = ArrayPool<byte>.Shared.Rent(PublicationObjectLimit + 1);
        try
        {
            var scanned = await InspectPublicationGitAsync(["cat-file", "--batch"], token,
                string.Join("\n", blobs.Select(blob => blob.Id)) + "\n", async (stream, cancellation) =>
                {
                    foreach (var blob in blobs)
                    {
                        var header = Encoding.ASCII.GetBytes($"{blob.Id} blob {blob.Size.ToString(CultureInfo.InvariantCulture)}\n");
                        await stream.ReadExactlyAsync(buffer.AsMemory(0, header.Length), cancellation).ConfigureAwait(false);
                        if (!header.AsSpan().SequenceEqual(buffer.AsSpan(0, header.Length)))
                            throw new InvalidDataException("Git object identity changed during inspection.");
                        await stream.ReadExactlyAsync(buffer.AsMemory(0, blob.Size + 1), cancellation).ConfigureAwait(false);
                        if (buffer[blob.Size] != (byte)'\n') throw new InvalidDataException("Git object boundary is invalid.");
                        refusal ??= InspectPublicationBlob(buffer.AsSpan(0, blob.Size), historicalAssetNames, historicalAssetHashes, blobHashes);
                    }
                    if (await stream.ReadAsync(buffer.AsMemory(0, 1), cancellation).ConfigureAwait(false) != 0)
                        throw new InvalidDataException("Unexpected Git object output.");
                }).ConfigureAwait(false);
            if (!scanned.Success) return PublicationRefusal("Cannot verify all Git object contents.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return PublicationRefusal("Git object contents could not be read safely.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
        if (refusal is not null) return PublicationRefusal(refusal);
        if (historicalAssetHashes.Overlaps(blobHashes))
            return PublicationRefusal("Git contains a blob matching an asset recorded in its metadata history.");
        return paths.Any(path => historicalAssetNames.Contains(Path.GetFileName(path)))
            ? PublicationRefusal("Git contains material referenced by a project in the inspected history.")
            : null;
    }

    private string? InspectPublicationBlob(ReadOnlySpan<byte> bytes, HashSet<string> historicalAssetNames,
        HashSet<string> historicalAssetHashes, HashSet<string> blobHashes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        blobHashes.Add(hash);
        if (publicationAssetHashes.Contains(hash))
            return "Git contains a blob matching a registered asset SHA-256.";
        string content;
        try { content = StrictPublicationUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return "Git contains binary or unverified encoded content."; }
        if (content.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')) ||
            content.StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal))
            return "Git contains binary content or an external large file pointer.";
        var start = content.AsSpan().TrimStart('\uFEFF').TrimStart();
        if (start.StartsWith("WEBVTT", StringComparison.Ordinal) &&
            (start.Length == 6 || char.IsWhiteSpace(start[6])))
            return "Git contains a WebVTT media asset.";

        // Git may canonicalize text to LF. Compare exact bytes and uniform line-ending
        // variants so a known caption does not escape inspection after normalization.
        if (content.Contains('\n'))
        {
            var lf = content.Replace("\r\n", "\n", StringComparison.Ordinal);
            var lfHash = Convert.ToHexString(SHA256.HashData(StrictPublicationUtf8.GetBytes(lf)));
            var crlfHash = Convert.ToHexString(SHA256.HashData(StrictPublicationUtf8.GetBytes(lf.Replace("\n", "\r\n", StringComparison.Ordinal))));
            blobHashes.Add(lfHash);
            blobHashes.Add(crlfHash);
            if (publicationAssetHashes.Contains(lfHash) || publicationAssetHashes.Contains(crlfHash))
                return "Git contains a blob matching a registered asset after line-ending normalization.";
        }

        if (start.StartsWith("{"))
        {
            try
            {
                using var document = JsonDocument.Parse(content.TrimStart('\uFEFF'));
                var root = document.RootElement;
                if (IsPrivateRelinkRecord(root))
                    return "Git contains a local relink recovery or publication record.";
                ReadProjectAssetNames(root, historicalAssetNames);
                if (root.TryGetProperty("Assets", out var assets) && assets.ValueKind == JsonValueKind.Array &&
                    root.TryGetProperty("ProjectId", out _))
                    foreach (var asset in assets.EnumerateArray())
                        ReadAsset(asset, "RelativePath", historicalAssetNames, historicalAssetHashes);
                if (root.TryGetProperty("DistributionFileName", out _))
                {
                    if (root.TryGetProperty("OriginalFileName", out _) &&
                        root.TryGetProperty("OriginalReference", out var reference) &&
                        reference.ValueKind == JsonValueKind.String && reference.GetString() is { } original &&
                        IsMachineLocalReference(original))
                        return "Git contains a pool record that exposes a non-portable original path.";
                    ReadAsset(root, "OriginalFileName", historicalAssetNames, historicalAssetHashes);
                    ReadAsset(root, "DistributionFileName", historicalAssetNames, historicalAssetHashes);
                }
            }
            catch (JsonException) { /* Ordinary text may contain a JSON fragment. */ }
        }
        return null;
    }

    private static bool IsPrivateRelinkRecord(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("Version", out _) &&
        root.TryGetProperty("OperationId", out _) &&
        root.TryGetProperty("OutputSha256", out _) &&
        (root.TryGetProperty("Identity", out _) ||
            (root.TryGetProperty("SourceSha256", out _) && root.TryGetProperty("Approved", out _)));

    private static bool IsMachineLocalReference(string reference)
    {
        var path = reference.Trim();
        return Path.IsPathRooted(path) || (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile);
    }

    private static void ReadProjectAssetNames(JsonElement node, HashSet<string> names)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                if (property.NameEquals("FilePath") && property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString() is { } reference)
                    names.Add(Path.GetFileName(PackagingDetector.NormalizeFilePathKey(reference)));
                else ReadProjectAssetNames(property.Value, names);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) ReadProjectAssetNames(item, names);
    }

    private static void ReadAsset(JsonElement entry, string nameProperty, HashSet<string> names, HashSet<string> hashes)
    {
        if (entry.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty("Sha256", out var hash) || hash.ValueKind != JsonValueKind.String ||
            hash.GetString() is not { Length: 64 } value || !value.All(Uri.IsHexDigit))
            return;
        hashes.Add(value);
        if (entry.TryGetProperty(nameProperty, out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { } path)
            names.Add(Path.GetFileName(PackagingDetector.NormalizeFilePathKey(path)));
    }
}
