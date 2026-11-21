using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YMMProjectManager.Infrastructure;

public sealed record AssetPoolRecord(int SchemaVersion, string OriginalFileName, string DistributionFileName,
    string Sha256, [property: JsonRequired] long Size, string OriginalReference, string CommitId, string? DownloadUrl,
    string? RedistributionNote);

public sealed record AssetPoolEntry(AssetPoolRecord Record, string ManifestPath, string PayloadPath);

public sealed class AssetPoolMatch
{
    internal AssetPoolMatch(string candidatePath, string? manifestPath, string? manifestSha256)
    {
        CandidatePath = candidatePath;
        ManifestPath = manifestPath;
        ManifestSha256 = manifestSha256;
    }

    public string CandidatePath { get; }
    public string? ManifestPath { get; }
    internal string? ManifestSha256 { get; }
}

/// <summary>Holds the verified files and directory chain through an explicitly confirmed relink.</summary>
public sealed class AssetPoolVerification : IAsyncDisposable
{
    private readonly OwnedFilePublication.DirectoryFence directories;
    private readonly FileStream payload;
    private readonly FileStream? manifest;

    internal AssetPoolVerification(string path, OwnedFilePublication.DirectoryFence directories, FileStream payload, FileStream? manifest)
    {
        CandidatePath = path;
        this.directories = directories;
        this.payload = payload;
        this.manifest = manifest;
    }

    public string CandidatePath { get; }

    public async ValueTask DisposeAsync()
    {
        try
        {
            try { await payload.DisposeAsync().ConfigureAwait(false); }
            finally { if (manifest is not null) await manifest.DisposeAsync().ConfigureAwait(false); }
        }
        finally { directories.Dispose(); }
    }
}

/// <summary>Collects selected assets into an existing folder and searches only approved folders.</summary>
public static class AssetPool
{
    public const string ManifestFileName = "asset-pool.json";
    private const string PendingPrefix = ".asset-pool-pending-";
    private const int MaxManifestBytes = 32 * 1024;
    private const int MaxPoolEntriesPerRoot = 512;
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        WriteIndented = true,
    };

    public static Task<AssetPoolEntry> CollectAsync(string approvedPoolRoot, string approvedSourceFile,
        string originalReference, string commitId, string? downloadUrl = null, string? redistributionNote = null,
        CancellationToken token = default) =>
        CollectCoreAsync(approvedPoolRoot, approvedSourceFile, originalReference, commitId, downloadUrl,
            redistributionNote, beforePublish: null, token);

    internal static async Task<AssetPoolEntry> CollectCoreAsync(string approvedPoolRoot, string approvedSourceFile,
        string originalReference, string commitId, string? downloadUrl, string? redistributionNote,
        Action? beforePublish, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var root = FullOrdinaryPath(approvedPoolRoot);
        var sourcePath = FullOrdinaryPath(approvedSourceFile);
        var name = GetOriginalFileName(originalReference);
        if (!SameName(name, Path.GetFileName(sourcePath)))
            throw new InvalidDataException("選択した素材と元の参照のファイル名が一致しません。");
        ValidateRecordFields(originalReference, commitId, downloadUrl, redistributionNote);

        using var rootFence = OwnedFilePublication.DirectoryFence.Open(root, enforceShare: true);
        using var sourceFence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(sourcePath)!, enforceShare: true);
        await using var source = OpenRead(sourcePath);
        var size = source.Length;
        var sha256 = await HashAsync(source, token).ConfigureAwait(false);
        source.Position = 0;

        var record = new AssetPoolRecord(1, name, FormatDistributionName(name, sha256), sha256, size,
            originalReference, commitId.ToLowerInvariant(), downloadUrl, redistributionNote);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, ManifestOptions);
        if (bytes.Length > MaxManifestBytes) throw new InvalidDataException("素材プールの記録が大きすぎます。");
        var destination = Path.Combine(root, FindPrefix(name, sha256) + Convert.ToHexString(SHA256.HashData(bytes)));
        var entry = new AssetPoolEntry(record, Path.Combine(destination, ManifestFileName),
            Path.Combine(destination, record.DistributionFileName));
        if (Directory.Exists(destination))
            return await VerifyExistingAsync(entry, token).ConfigureAwait(false);

        using var stage = OwnedStagingDirectory.Reserve(root, PendingPrefix);
        try
        {
            await PublishEntryAsync(stage, source, entry, bytes, beforePublish, token).ConfigureAwait(false);
        }
        catch (IOException ex) when (IsNameCollision(ex) || Directory.Exists(destination))
        {
            // The other collector may have published the exact same record first.
            // A different or incomplete entry is preserved and reported.
            return await VerifyExistingAsync(entry, token).ConfigureAwait(false);
        }
        return await VerifyExistingAsync(entry, CancellationToken.None).ConfigureAwait(false);
    }

    public static async Task<AssetPoolRecord> LoadAsync(string selectedManifestPath, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var manifestPath = FullOrdinaryPath(selectedManifestPath);
        ValidateManifestName(manifestPath);
        using var directories = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(manifestPath)!, enforceShare: true);
        await using var manifest = OpenRead(manifestPath);
        return (await ReadManifestAsync(manifest, token).ConfigureAwait(false)).Record;
    }

    public static async Task<IReadOnlyList<AssetPoolMatch>> FindAsync(string expectedOriginalFileName, string? expectedSha256,
        IReadOnlyList<string> approvedSearchRoots, string? selectedManifestPath = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(approvedSearchRoots);
        if (string.IsNullOrEmpty(expectedSha256)) return [];
        ValidateExpectation(expectedOriginalFileName, expectedSha256);
        var roots = approvedSearchRoots.Select(FullOrdinaryPath).Distinct(StringComparer.Ordinal).ToArray();
        var matches = new Dictionary<string, AssetPoolMatch>(StringComparer.Ordinal);
        if (selectedManifestPath is not null)
        {
            var manifest = FullOrdinaryPath(selectedManifestPath);
            ValidateManifestName(manifest);
            if (!roots.Any(root => IsDescendant(root, manifest)))
                throw new InvalidDataException("素材プールの記録は許可した検索フォルダー内から選択してください。");
            var match = await MatchManifestAsync(manifest, expectedOriginalFileName, expectedSha256, token).ConfigureAwait(false);
            if (match is not null) matches.Add(match.CandidatePath, match);
            return matches.Values.ToArray();
        }

        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            using var fence = OwnedFilePublication.DirectoryFence.Open(root, enforceShare: true);
            var ordinary = Path.Combine(root, expectedOriginalFileName);
            if (await MatchesFileAsync(ordinary, expectedSha256, null, token).ConfigureAwait(false))
                matches.TryAdd(ordinary, new AssetPoolMatch(ordinary, null, null));

            var count = 0;
            foreach (var directory in Directory.EnumerateDirectories(root, FindPrefix(expectedOriginalFileName, expectedSha256) + "*"))
            {
                token.ThrowIfCancellationRequested();
                if (++count > MaxPoolEntriesPerRoot) throw new InvalidDataException("候補が多すぎます。素材プールの記録を指定してください。");
                var manifest = Path.Combine(directory, ManifestFileName);
                var match = await MatchManifestAsync(manifest, expectedOriginalFileName, expectedSha256, token).ConfigureAwait(false);
                if (match is not null) matches.TryAdd(match.CandidatePath, match);
            }
        }
        return matches.Values.ToArray();
    }

    public static async Task<AssetPoolVerification> OpenVerifiedAsync(AssetPoolMatch match, string expectedOriginalFileName,
        string expectedSha256, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(match);
        ValidateExpectation(expectedOriginalFileName, expectedSha256);
        var directory = Path.GetDirectoryName(match.CandidatePath)!;
        var fence = OwnedFilePublication.DirectoryFence.Open(directory, enforceShare: true);
        FileStream? manifest = null;
        FileStream? payload = null;
        try
        {
            long? size = null;
            if (match.ManifestPath is not null)
            {
                manifest = OpenRead(match.ManifestPath);
                var (record, manifestSha256) = await ReadManifestAsync(manifest, token).ConfigureAwait(false);
                if (!SameName(manifestSha256, match.ManifestSha256) || !IsExpected(record, expectedOriginalFileName, expectedSha256) ||
                    !SameName(record.DistributionFileName, Path.GetFileName(match.CandidatePath)))
                    throw new InvalidDataException("選択後に素材プールの記録が変更されました。候補を検索し直してください。");
                size = record.Size;
            }
            else if (!SameName(Path.GetFileName(match.CandidatePath), expectedOriginalFileName))
                throw new InvalidDataException("選択した素材のファイル名が記録と一致しません。");

            payload = OpenRead(match.CandidatePath);
            if (!await MatchesStreamAsync(payload, expectedSha256, size, token).ConfigureAwait(false))
                throw new InvalidDataException("選択した素材の内容が記録と一致しません。");
            return new AssetPoolVerification(match.CandidatePath, fence, payload, manifest);
        }
        catch
        {
            payload?.Dispose();
            manifest?.Dispose();
            fence.Dispose();
            throw;
        }
    }

    /// <summary>Extracts a literal basename without resolving or accessing the reference.</summary>
    public static string GetOriginalFileName(string originalReference)
    {
        ValidateText(originalReference, 4096, "元の素材参照");
        var leaf = originalReference[(originalReference.LastIndexOfAny(['\\', '/']) + 1)..];
        if (!AssetInventory.IsOrdinaryPathComponent(leaf))
            throw new InvalidDataException("元の素材参照に有効なファイル名がありません。");
        return leaf;
    }

    private static async Task PublishEntryAsync(OwnedStagingDirectory stage, FileStream source, AssetPoolEntry entry,
        byte[] manifestBytes, Action? beforePublish, CancellationToken token)
    {
        FileStream? payload = null;
        FileStream? manifest = null;
        (ulong Volume, Guid FileId)? payloadIdentity = null;
        (ulong Volume, Guid FileId)? manifestIdentity = null;
        var payloadPath = Path.Combine(stage.Path, entry.Record.DistributionFileName);
        var manifestPath = Path.Combine(stage.Path, ManifestFileName);
        var published = false;
        try
        {
            payload = OwnedFilePublication.CreateOwnedOutputFile(payloadPath, readBack: true);
            await source.CopyToAsync(payload, token).ConfigureAwait(false);
            if (payload.Length != entry.Record.Size) throw new IOException("収集中に元の素材ファイルの長さが変わりました。");
            await FlushAsync(payload, token).ConfigureAwait(false);
            payload.Position = 0;
            if (!await MatchesStreamAsync(payload, entry.Record.Sha256, entry.Record.Size, token).ConfigureAwait(false))
                throw new IOException("コピーした素材を検証できません。");
            manifest = OwnedFilePublication.CreateOwnedOutputFile(manifestPath);
            await manifest.WriteAsync(manifestBytes, token).ConfigureAwait(false);
            await FlushAsync(manifest, token).ConfigureAwait(false);
            beforePublish?.Invoke();
            token.ThrowIfCancellationRequested();
            payloadIdentity = ProjectFileEvidence.ReadPhysicalIdentity(payload.SafeFileHandle);
            manifestIdentity = ProjectFileEvidence.ReadPhysicalIdentity(manifest.SafeFileHandle);
            // Windows refuses to rename the parent while these child handles are open.
            // After the rename the caller reopens the published files and verifies their contents.
            payload.Dispose();
            payload = null;
            manifest.Dispose();
            manifest = null;
            stage.Publish(Path.GetDirectoryName(entry.ManifestPath)!, token);
            published = true;
        }
        finally
        {
            if (!published)
            {
                if (manifest is not null) OwnedFilePublication.TryDeleteOpenedPath(manifest.SafeFileHandle);
                if (payload is not null) OwnedFilePublication.TryDeleteOpenedPath(payload.SafeFileHandle);
            }
            manifest?.Dispose();
            payload?.Dispose();
            if (!published)
            {
                // A disconnection may prevent immediate cleanup. Never recursively delete
                // any path or remove a child file not owned by this operation.
                try
                {
                    if (payloadIdentity is not null || manifestIdentity is not null)
                    {
                        using var pin = stage.PinRecoveryPath();
                        TryDeleteVerifiedFile(payloadPath, payloadIdentity);
                        TryDeleteVerifiedFile(manifestPath, manifestIdentity);
                    }
                    stage.DeleteIfEmpty();
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void TryDeleteVerifiedFile(string path, (ulong Volume, Guid FileId)? identity)
    {
        if (identity is null) return;
        try
        {
            using var handle = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename: true);
            if (ProjectFileEvidence.ReadPhysicalIdentity(handle) == identity.Value)
                OwnedFilePublication.TryDeleteOpenedPath(handle);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task FlushAsync(FileStream file, CancellationToken token)
    {
        await file.FlushAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        file.Flush(flushToDisk: true);
    }

    private static async Task<AssetPoolEntry> VerifyExistingAsync(AssetPoolEntry entry, CancellationToken token)
    {
        using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(entry.ManifestPath)!, enforceShare: true);
        await using var manifest = OpenRead(entry.ManifestPath);
        if ((await ReadManifestAsync(manifest, token).ConfigureAwait(false)).Record != entry.Record ||
            !await MatchesFileAsync(entry.PayloadPath, entry.Record.Sha256, entry.Record.Size, token).ConfigureAwait(false))
            throw new IOException("同じ保管先に内容の異なる素材プール項目があります。既存内容を保持しました。");
        return entry;
    }

    private static async Task<AssetPoolMatch?> MatchManifestAsync(string manifestPath, string originalFileName, string sha256, CancellationToken token)
    {
        using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(manifestPath)!, enforceShare: true);
        await using var manifest = OpenRead(manifestPath);
        var (record, manifestSha256) = await ReadManifestAsync(manifest, token).ConfigureAwait(false);
        if (!IsExpected(record, originalFileName, sha256)) return null;
        var candidate = Path.Combine(Path.GetDirectoryName(manifestPath)!, record.DistributionFileName);
        return await MatchesFileAsync(candidate, sha256, record.Size, token).ConfigureAwait(false)
            ? new AssetPoolMatch(candidate, manifestPath, manifestSha256) : null;
    }

    private static async Task<(AssetPoolRecord Record, string ManifestSha256)> ReadManifestAsync(FileStream manifest, CancellationToken token)
    {
        if (manifest.Length is <= 0 or > MaxManifestBytes) throw new InvalidDataException("素材プールの記録サイズが無効です。");
        var bytes = new byte[checked((int)manifest.Length)];
        await manifest.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        var record = JsonSerializer.Deserialize<AssetPoolRecord>(bytes, ManifestOptions)
            ?? throw new InvalidDataException("素材プールの記録が空です。");
        if (record.SchemaVersion != 1 || record.Size < 0)
            throw new InvalidDataException("素材プールの記録形式を認識できません。");
        ValidateExpectation(record.OriginalFileName, record.Sha256);
        ValidateRecordFields(record.OriginalReference, record.CommitId, record.DownloadUrl, record.RedistributionNote);
        if (!SameName(GetOriginalFileName(record.OriginalReference), record.OriginalFileName) ||
            !SameName(FormatDistributionName(record.OriginalFileName, record.Sha256), record.DistributionFileName))
            throw new InvalidDataException("素材プールの記録の元ファイル名または配布名が無効です。");
        return (record, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static async Task<bool> MatchesFileAsync(string path, string sha256, long? size, CancellationToken token)
    {
        try
        {
            await using var stream = OpenRead(path);
            return await MatchesStreamAsync(stream, sha256, size, token).ConfigureAwait(false);
        }
        catch (IOException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 2 or 3 }) { return false; }
    }

    private static async Task<bool> MatchesStreamAsync(FileStream stream, string sha256, long? size, CancellationToken token)
    {
        if (size is not null && stream.Length != size) return false;
        return SameName(await HashAsync(stream, token).ConfigureAwait(false), sha256);
    }

    private static async Task<string> HashAsync(FileStream stream, CancellationToken token) =>
        Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));

    private static FileStream OpenRead(string path)
    {
        var handle = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename: false);
        try { return new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }

    private static string FullOrdinaryPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('/', '\\');
        ReadOrdinaryRoot(normalized);
        var full = Path.GetFullPath(normalized);
        var root = ReadOrdinaryRoot(full);
        foreach (var part in full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (!AssetInventory.IsOrdinaryPathComponent(part)) throw new ArgumentException("パスに無効な名前が含まれています。", nameof(path));
        return full;
    }

    private static string ReadOrdinaryRoot(string path)
    {
        if (Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) &&
            !path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            var root = Path.GetPathRoot(path)!;
            if (root.Length == 3 && char.IsAsciiLetter(root[0]) && root[1] == ':' && root[2] == '\\')
                return root;
            if (root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var unc = root[2..].TrimEnd('\\').Split('\\');
                if (unc.Length == 2 && unc.All(AssetInventory.IsOrdinaryPathComponent)) return root;
            }
        }
        throw new ArgumentException("通常のドライブまたは UNC の絶対パスを選択してください。", nameof(path));
    }

    private static void ValidateManifestName(string path)
    {
        if (!SameName(Path.GetFileName(path), ManifestFileName))
            throw new InvalidDataException($"素材プールの記録は {ManifestFileName} を選択してください。");
    }

    private static bool IsDescendant(string root, string path) =>
        // Windows also supports case-sensitive directories. A case-insensitive string comparison
        // would accept a path under an unapproved sibling on such a volume.
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static bool IsNameCollision(IOException exception) =>
        exception.InnerException is Win32Exception { NativeErrorCode: 80 or 183 };

    private static bool SameName(string left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsExpected(AssetPoolRecord record, string expectedName, string expectedSha256) =>
        SameName(record.OriginalFileName, expectedName) && SameName(record.Sha256, expectedSha256);

    private static void ValidateExpectation(string? filename, string? sha256)
    {
        if (filename is null || !AssetInventory.IsOrdinaryPathComponent(filename) || sha256 is not { Length: 64 } ||
            !sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("素材の照合には元ファイル名と完全な SHA-256 が必要です。");
    }

    private static void ValidateRecordFields(string originalReference, string? commitId, string? url, string? redistributionNote)
    {
        ValidateText(originalReference, 4096, "元の素材参照");
        if (commitId is not { Length: 40 or 64 } || !commitId.All(Uri.IsHexDigit) || commitId.All(character => character == '0'))
            throw new InvalidDataException("素材を使用したコミットの完全な OID が必要です。");
        AssetInventory.ValidateUrl(url);
        if (redistributionNote is not null) ValidateText(redistributionNote, 2048, "再配布許諾の記録");
    }

    private static void ValidateText(string? value, int maximum, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new InvalidDataException($"{field}が空か規定の長さを超えているか、制御文字を含みます。");
    }

    private static string FindPrefix(string filename, string sha256)
    {
        var key = Encoding.UTF8.GetBytes(filename.ToUpperInvariant() + "\n" + sha256.ToUpperInvariant());
        return "asset-" + Convert.ToHexString(SHA256.HashData(key))[..16] + "-";
    }

    private static string FormatDistributionName(string filename, string sha256)
    {
        const int suffixLength = 16;
        var extension = Path.GetExtension(filename);
        var stem = Path.GetFileNameWithoutExtension(filename);
        var length = Math.Min(stem.Length, 255 - extension.Length - suffixLength - 1);
        if (length < 0) throw new InvalidDataException("配布用の名前を付加できない長さの拡張子です。");
        if (length > 0 && char.IsHighSurrogate(stem[length - 1])) length--;
        var result = stem[..length] + "_" + sha256[..suffixLength].ToUpperInvariant() + extension;
        if (!AssetInventory.IsOrdinaryPathComponent(result)) throw new InvalidDataException("配布用の名前が無効です。");
        return result;
    }
}
