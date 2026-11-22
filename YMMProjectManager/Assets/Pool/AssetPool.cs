using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YMMProjectManager.Infrastructure;

//CSJ,record=AssetPoolRecord,素材プール記録
public sealed record AssetPoolRecord(int SchemaVersion, string OriginalFileName, string DistributionFileName,
    string Sha256, [property: JsonRequired] long Size, string OriginalReference, string CommitId, string? DownloadUrl,
    string? RedistributionNote);

//CSJ,record=AssetPoolEntry,素材プール項目
public sealed record AssetPoolEntry(AssetPoolRecord Record, string ManifestPath, string PayloadPath);

//CSJ,class=AssetPoolMatch,素材プール一致
public sealed class AssetPoolMatch
{
    internal AssetPoolMatch(string candidatePath, string? manifestPath, string? manifestSha256)
    {
        CandidatePath = candidatePath;
        ManifestPath = manifestPath;
        ManifestSha256 = manifestSha256;
    }

    //CSJ,property=CandidatePath,候補パス
    public string CandidatePath { get; }
    //CSJ,property=ManifestPath,目録パス
    public string? ManifestPath { get; }
    //CSJ,property=ManifestSha256,目録SHA-256
    internal string? ManifestSha256 { get; }
}

/// <summary>Holds the verified files and directory chain through an explicitly confirmed relink.</summary>
//CSJ,class=AssetPoolVerification,素材プール検証
public sealed class AssetPoolVerification : IAsyncDisposable
{
    //CSJ,field=directories,ディレクトリ一覧
    private readonly OwnedFilePublication.DirectoryFence directories;
    //CSJ,field=payload,本体データ
    private readonly FileStream payload;
    //CSJ,field=manifest,目録
    private readonly FileStream? manifest;

    internal AssetPoolVerification(string path, OwnedFilePublication.DirectoryFence directories, FileStream payload, FileStream? manifest)
    {
        CandidatePath = path;
        this.directories = directories;
        this.payload = payload;
        this.manifest = manifest;
    }

    //CSJ,property=CandidatePath,候補パス
    public string CandidatePath { get; }

    //CSJ,method=DisposeAsync,破棄する（非同期）
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
//CSJ,class=AssetPool,素材の収集と再利用
public static class AssetPool
{
    //CSJ,field=ManifestFileName,目録ファイル名
    public const string ManifestFileName = "asset-pool.json";
    //CSJ,field=PendingPrefix,保留接頭辞
    private const string PendingPrefix = ".asset-pool-pending-";
    //CSJ,field=MaxManifestBytes,最大目録バイト列
    private const int MaxManifestBytes = 32 * 1024;
    private const int MaxPoolEntriesPerRoot = 512;
    //CSJ,field=ManifestOptions,目録オプション
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        WriteIndented = true,
    };

    //CSJ,method=CollectAsync,収集する（非同期）
    public static Task<AssetPoolEntry> CollectAsync(string approvedPoolRoot, string approvedSourceFile,
        //CSJ,parameter=originalReference,元の参照
        string originalReference, string commitId, string? downloadUrl = null, string? redistributionNote = null,
        CancellationToken token = default) =>
        CollectCoreAsync(approvedPoolRoot, approvedSourceFile, originalReference, commitId, downloadUrl,
            redistributionNote, beforePublish: null, token);

    //CSJ,method=CollectCoreAsync,中核を収集する（非同期）
    internal static async Task<AssetPoolEntry> CollectCoreAsync(string approvedPoolRoot, string approvedSourceFile,
        //CSJ,parameter=originalReference,元の参照
        string originalReference, string commitId, string? downloadUrl, string? redistributionNote,
        //CSJ,parameter=beforePublish,確定反映前の処理
        Action? beforePublish, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        //CSJ,variable=root,ルート
        var root = FullOrdinaryPath(approvedPoolRoot);
        //CSJ,variable=sourcePath,元のパス
        var sourcePath = FullOrdinaryPath(approvedSourceFile);
        //CSJ,variable=name,名前
        var name = GetOriginalFileName(originalReference);
        if (!SameFileName(name, Path.GetFileName(sourcePath)))
            throw new InvalidDataException("選択した素材と元の参照のファイル名が一致しません。");
        ValidateRecordFields(originalReference, commitId, downloadUrl, redistributionNote);

        //CSJ,variable=rootFence,ルート整合性境界
        using var rootFence = OwnedFilePublication.DirectoryFence.Open(root, enforceShare: true);
        //CSJ,variable=sourceFence,元整合性境界
        using var sourceFence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(sourcePath)!, enforceShare: true);
        await using var source = OpenRead(sourcePath);
        //CSJ,variable=size,サイズ
        var size = source.Length;
        var sha256 = await AssetContentHash.ReadSha256Async(source, token).ConfigureAwait(false);
        source.Position = 0;

        //CSJ,variable=record,記録
        var record = new AssetPoolRecord(1, name, FormatDistributionName(name, sha256), sha256, size,
            originalReference, commitId.ToLowerInvariant(), downloadUrl, redistributionNote);
        //CSJ,variable=bytes,バイト列
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, ManifestOptions);
        if (bytes.Length > MaxManifestBytes) throw new InvalidDataException("素材プールの記録が大きすぎます。");
        //CSJ,variable=destination,格納先
        var destination = Path.Combine(root, FindPrefix(name, sha256) + Convert.ToHexString(SHA256.HashData(bytes)));
        var entry = new AssetPoolEntry(record, Path.Combine(destination, ManifestFileName),
            Path.Combine(destination, record.DistributionFileName));
        if (Directory.Exists(destination))
            return await VerifyExistingAsync(entry, token).ConfigureAwait(false);

        //CSJ,variable=stage,作業段階
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

    //CSJ,method=LoadAsync,読み込む（非同期）
    public static async Task<AssetPoolRecord> LoadAsync(string selectedManifestPath, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        //CSJ,variable=manifestPath,目録パス
        var manifestPath = FullOrdinaryPath(selectedManifestPath);
        ValidateManifestName(manifestPath);
        //CSJ,variable=directories,ディレクトリ一覧
        using var directories = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(manifestPath)!, enforceShare: true);
        //CSJ,variable=manifest,目録
        await using var manifest = OpenRead(manifestPath);
        return (await ReadManifestAsync(manifest, token).ConfigureAwait(false)).Record;
    }

    //CSJ,method=FindAsync,探す（非同期）
    public static async Task<IReadOnlyList<AssetPoolMatch>> FindAsync(string expectedOriginalFileName, string? expectedSha256,
        IReadOnlyList<string> approvedSearchRoots, string? selectedManifestPath = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(approvedSearchRoots);
        if (string.IsNullOrEmpty(expectedSha256)) return [];
        ValidateExpectation(expectedOriginalFileName, expectedSha256);
        var roots = approvedSearchRoots.Select(FullOrdinaryPath).Distinct(StringComparer.Ordinal).ToArray();
        //CSJ,variable=matches,一致項目
        var matches = new Dictionary<string, AssetPoolMatch>(StringComparer.Ordinal);
        if (selectedManifestPath is not null)
        {
            //CSJ,variable=manifest,目録
            var manifest = FullOrdinaryPath(selectedManifestPath);
            ValidateManifestName(manifest);
            if (!roots.Any(root => IsDescendant(root, manifest)))
                throw new InvalidDataException("素材プールの記録は許可した検索フォルダー内から選択してください。");
            //CSJ,variable=match,一致
            var match = await MatchManifestAsync(manifest, expectedOriginalFileName, expectedSha256, token).ConfigureAwait(false);
            if (match is not null) matches.Add(match.CandidatePath, match);
            return matches.Values.ToArray();
        }

        //CSJ,variable=root,ルート
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            //CSJ,variable=fence,整合性境界
            using var fence = OwnedFilePublication.DirectoryFence.Open(root, enforceShare: true);
            //CSJ,variable=ordinary,通常
            var ordinary = Path.Combine(root, expectedOriginalFileName);
            if (await MatchesFileAsync(ordinary, expectedSha256, null, token).ConfigureAwait(false))
                matches.TryAdd(ordinary, new AssetPoolMatch(ordinary, null, null));

            //CSJ,variable=count,件数
            var count = 0;
            //CSJ,variable=directory,ディレクトリ
            foreach (var directory in Directory.EnumerateDirectories(root, FindPrefix(expectedOriginalFileName, expectedSha256) + "*"))
            {
                token.ThrowIfCancellationRequested();
                if (++count > MaxPoolEntriesPerRoot) throw new InvalidDataException("候補が多すぎます。素材プールの記録を指定してください。");
                //CSJ,variable=manifest,目録
                var manifest = Path.Combine(directory, ManifestFileName);
                //CSJ,variable=match,一致
                var match = await MatchManifestAsync(manifest, expectedOriginalFileName, expectedSha256, token).ConfigureAwait(false);
                if (match is not null) matches.TryAdd(match.CandidatePath, match);
            }
        }
        return matches.Values.ToArray();
    }

    //CSJ,method=OpenVerifiedAsync,検証済みを開く（非同期）
    public static async Task<AssetPoolVerification> OpenVerifiedAsync(AssetPoolMatch match, string expectedOriginalFileName,
        string expectedSha256, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(match);
        ValidateExpectation(expectedOriginalFileName, expectedSha256);
        //CSJ,variable=candidatePath,候補パス
        var candidatePath = FullOrdinaryPath(match.CandidatePath);
        if (candidatePath != match.CandidatePath)
            throw new InvalidDataException("候補のパスが確認時の形式と一致しません。");
        //CSJ,variable=directory,ディレクトリ
        var directory = Path.GetDirectoryName(candidatePath)!;
        if (match.ManifestPath is { } recordedManifest)
        {
            //CSJ,variable=manifestPath,目録パス
            var manifestPath = FullOrdinaryPath(recordedManifest);
            ValidateManifestName(manifestPath);
            if (manifestPath != recordedManifest || Path.GetDirectoryName(manifestPath) != directory ||
                string.Equals(candidatePath, manifestPath, StringComparison.OrdinalIgnoreCase) ||
                match.ManifestSha256 is not { Length: 64 } digest || !digest.All(Uri.IsHexDigit))
                throw new InvalidDataException("候補と素材プールの記録の位置または照合証拠が無効です。");
        }
        else if (match.ManifestSha256 is not null)
            throw new InvalidDataException("素材プールの記録パスのない照合証拠です。");

        //CSJ,variable=fence,整合性境界
        var fence = OwnedFilePublication.DirectoryFence.Open(directory, enforceShare: true);
        //CSJ,variable=manifest,目録
        FileStream? manifest = null;
        //CSJ,variable=payload,本体データ
        FileStream? payload = null;
        try
        {
            //CSJ,variable=size,サイズ
            long? size = null;
            if (match.ManifestPath is not null)
            {
                manifest = OpenRead(match.ManifestPath);
                var (record, manifestSha256) = await ReadManifestAsync(manifest, token).ConfigureAwait(false);
                if (!string.Equals(manifestSha256, match.ManifestSha256, StringComparison.OrdinalIgnoreCase) || !IsExpected(record, expectedOriginalFileName, expectedSha256) ||
                    !SameFileName(record.DistributionFileName, Path.GetFileName(match.CandidatePath)))
                    throw new InvalidDataException("選択後に素材プールの記録が変更されました。候補を検索し直してください。");
                size = record.Size;
            }
            else if (!SameFileName(Path.GetFileName(match.CandidatePath), expectedOriginalFileName))
                throw new InvalidDataException("選択した素材のファイル名が記録と一致しません。");

            payload = OpenRead(candidatePath);
            if (!await MatchesStreamAsync(payload, expectedSha256, size, token).ConfigureAwait(false))
                throw new InvalidDataException("選択した素材の内容が記録と一致しません。");
            return new AssetPoolVerification(candidatePath, fence, payload, manifest);
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
    //CSJ,method=GetOriginalFileName,元のファイル名を取得する
    public static string GetOriginalFileName(string originalReference)
    {
        ValidateText(originalReference, 4096, "元の素材参照");
        var leaf = originalReference[(originalReference.LastIndexOfAny(['\\', '/']) + 1)..];
        if (!AssetInventory.IsOrdinaryPathComponent(leaf))
            throw new InvalidDataException("元の素材参照に有効なファイル名がありません。");
        return leaf;
    }

    //CSJ,method=PublishEntryAsync,項目を確定反映する（非同期）
    private static async Task PublishEntryAsync(OwnedStagingDirectory stage, FileStream source, AssetPoolEntry entry,
        //CSJ,parameter=manifestBytes,目録バイト列
        byte[] manifestBytes, Action? beforePublish, CancellationToken token)
    {
        //CSJ,variable=payload,本体データ
        FileStream? payload = null;
        //CSJ,variable=manifest,目録
        FileStream? manifest = null;
        //CSJ,variable=payloadIdentity,本体データ識別情報
        (ulong Volume, Guid FileId)? payloadIdentity = null;
        //CSJ,variable=manifestIdentity,目録識別情報
        (ulong Volume, Guid FileId)? manifestIdentity = null;
        //CSJ,variable=payloadPath,本体データパス
        var payloadPath = Path.Combine(stage.Path, entry.Record.DistributionFileName);
        //CSJ,variable=manifestPath,目録パス
        var manifestPath = Path.Combine(stage.Path, ManifestFileName);
        //CSJ,variable=published,確定済み
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
            VerifyEntryContents(stage.Path, entry.Record.DistributionFileName);
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
                        //CSJ,variable=pin,固定参照
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

    //CSJ,method=TryDeleteVerifiedFile,検証済みファイルの削除を試みる
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

    //CSJ,method=FlushAsync,書き出す（非同期）
    private static async Task FlushAsync(FileStream file, CancellationToken token)
    {
        await file.FlushAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        file.Flush(flushToDisk: true);
    }

    //CSJ,method=VerifyExistingAsync,既存を検証する（非同期）
    private static async Task<AssetPoolEntry> VerifyExistingAsync(AssetPoolEntry entry, CancellationToken token)
    {
        //CSJ,variable=directory,ディレクトリ
        var directory = Path.GetDirectoryName(entry.ManifestPath)!;
        //CSJ,variable=fence,整合性境界
        using var fence = OwnedFilePublication.DirectoryFence.Open(directory, enforceShare: true);
        //CSJ,variable=manifest,目録
        await using var manifest = OpenRead(entry.ManifestPath);
        //CSJ,variable=payload,本体データ
        await using var payload = OpenRead(entry.PayloadPath);
        if ((await ReadManifestAsync(manifest, token).ConfigureAwait(false)).Record != entry.Record ||
            !await MatchesStreamAsync(payload, entry.Record.Sha256, entry.Record.Size, token).ConfigureAwait(false))
            throw new IOException("同じ保管先に内容の異なる素材プール項目があります。既存内容を保持しました。");
        VerifyEntryContents(directory, entry.Record.DistributionFileName);
        return entry;
    }

    private static void VerifyEntryContents(string directory, string payloadName)
    {
        //CSJ,variable=names,名前一覧
        var names = Directory.EnumerateFileSystemEntries(directory).Take(3).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        if (!names.SetEquals([ManifestFileName, payloadName]))
            throw new IOException("素材プール項目に予定外のファイルがあります。既存内容を保持しました。");
    }

    //CSJ,method=MatchManifestAsync,目録を照合する（非同期）
    private static async Task<AssetPoolMatch?> MatchManifestAsync(string manifestPath, string originalFileName, string sha256, CancellationToken token)
    {
        //CSJ,variable=fence,整合性境界
        using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(manifestPath)!, enforceShare: true);
        //CSJ,variable=manifest,目録
        await using var manifest = OpenRead(manifestPath);
        var (record, manifestSha256) = await ReadManifestAsync(manifest, token).ConfigureAwait(false);
        if (!IsExpected(record, originalFileName, sha256)) return null;
        var candidate = Path.Combine(Path.GetDirectoryName(manifestPath)!, record.DistributionFileName);
        return await MatchesFileAsync(candidate, sha256, record.Size, token).ConfigureAwait(false)
            ? new AssetPoolMatch(candidate, manifestPath, manifestSha256) : null;
    }

    //CSJ,method=ReadManifestAsync,目録を読み取る（非同期）
    private static async Task<(AssetPoolRecord Record, string ManifestSha256)> ReadManifestAsync(FileStream manifest, CancellationToken token)
    {
        if (manifest.Length is <= 0 or > MaxManifestBytes) throw new InvalidDataException("素材プールの記録サイズが無効です。");
        //CSJ,variable=bytes,バイト列
        var bytes = new byte[checked((int)manifest.Length)];
        await manifest.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        //CSJ,variable=record,記録
        var record = JsonSerializer.Deserialize<AssetPoolRecord>(bytes, ManifestOptions)
            ?? throw new InvalidDataException("素材プールの記録が空です。");
        if (record.SchemaVersion != 1 || record.Size < 0)
            throw new InvalidDataException("素材プールの記録形式を認識できません。");
        ValidateExpectation(record.OriginalFileName, record.Sha256);
        ValidateRecordFields(record.OriginalReference, record.CommitId, record.DownloadUrl, record.RedistributionNote);
        if (!SameFileName(GetOriginalFileName(record.OriginalReference), record.OriginalFileName) ||
            !SameFileName(FormatDistributionName(record.OriginalFileName, record.Sha256), record.DistributionFileName))
            throw new InvalidDataException("素材プールの記録の元ファイル名または配布名が無効です。");
        return (record, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    //CSJ,method=MatchesFileAsync,ファイルが一致するか確認する（非同期）
    private static async Task<bool> MatchesFileAsync(string path, string sha256, long? size, CancellationToken token)
    {
        try
        {
            //CSJ,variable=stream,ストリーム
            await using var stream = OpenRead(path);
            return await MatchesStreamAsync(stream, sha256, size, token).ConfigureAwait(false);
        }
        catch (IOException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 2 or 3 }) { return false; }
    }

    //CSJ,method=MatchesStreamAsync,ストリームが一致するか確認する（非同期）
    private static async Task<bool> MatchesStreamAsync(FileStream stream, string sha256, long? size, CancellationToken token)
    {
        if (size is not null && stream.Length != size) return false;
        return string.Equals(await AssetContentHash.ReadSha256Async(stream, token).ConfigureAwait(false), sha256, StringComparison.OrdinalIgnoreCase);
    }


    //CSJ,method=OpenRead,読み取りを開く
    private static FileStream OpenRead(string path)
    {
        var handle = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename: false);
        try { return new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }

    //CSJ,method=FullOrdinaryPath,完全通常パス
    internal static string FullOrdinaryPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        //CSJ,variable=normalized,正規化済み
        var normalized = path.Replace('/', '\\');
        ReadOrdinaryRoot(normalized);
        //CSJ,variable=full,絶対パス
        var full = Path.GetFullPath(normalized);
        //CSJ,variable=root,ルート
        var root = ReadOrdinaryRoot(full);
        foreach (var part in full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (!AssetInventory.IsOrdinaryPathComponent(part)) throw new ArgumentException("パスに無効な名前が含まれています。", nameof(path));
        return full;
    }

    //CSJ,method=ReadOrdinaryRoot,通常ルートを読み取る
    private static string ReadOrdinaryRoot(string path)
    {
        if (Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) &&
            !path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            //CSJ,variable=root,ルート
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

    //CSJ,method=ValidateManifestName,目録名前を検証する
    private static void ValidateManifestName(string path)
    {
        if (!SameFileName(Path.GetFileName(path), ManifestFileName))
            throw new InvalidDataException($"素材プールの記録は {ManifestFileName} を選択してください。");
    }

    private static bool IsDescendant(string root, string path) =>
        // Windows also supports case-sensitive directories. A case-insensitive string comparison
        // would accept a path under an unapproved sibling on such a volume.
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    //CSJ,method=IsNameCollision,名前衝突かを判定する
    private static bool IsNameCollision(IOException exception) =>
        exception.InnerException is Win32Exception { NativeErrorCode: 80 or 183 };

    //CSJ,method=SameFileName,ファイル名が同一か確認する
    private static bool SameFileName(string left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    //CSJ,method=IsExpected,期待値かを判定する
    private static bool IsExpected(AssetPoolRecord record, string expectedName, string expectedSha256) =>
        SameFileName(record.OriginalFileName, expectedName) && string.Equals(record.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase);

    private static void ValidateExpectation(string? filename, string? sha256)
    {
        if (filename is null || !AssetInventory.IsOrdinaryPathComponent(filename) || sha256 is not { Length: 64 } ||
            !sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("素材の照合には元ファイル名と完全な SHA-256 が必要です。");
    }

    //CSJ,method=ValidateRecordFields,記録フィールド一覧を検証する
    private static void ValidateRecordFields(string originalReference, string? commitId, string? url, string? redistributionNote)
    {
        ValidateText(originalReference, 4096, "元の素材参照");
        if (commitId is not { Length: 40 or 64 } || !commitId.All(Uri.IsHexDigit) || commitId.All(character => character == '0'))
            throw new InvalidDataException("素材を使用したコミットの完全な OID が必要です。");
        AssetInventory.ValidateUrl(url);
        if (redistributionNote is not null) ValidateText(redistributionNote, 2048, "再配布許諾の記録");
    }

    //CSJ,method=ValidateText,テキストを検証する
    private static void ValidateText(string? value, int maximum, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new InvalidDataException($"{field}が空か規定の長さを超えているか、制御文字を含みます。");
    }

    //CSJ,method=FindPrefix,接頭辞を探す
    private static string FindPrefix(string filename, string sha256)
    {
        //CSJ,variable=key,キー
        var key = Encoding.UTF8.GetBytes(filename.ToUpperInvariant() + "\n" + sha256.ToUpperInvariant());
        return "asset-" + Convert.ToHexString(SHA256.HashData(key))[..16] + "-";
    }

    private static string FormatDistributionName(string filename, string sha256)
    {
        //CSJ,variable=suffixLength,接尾辞長さ
        const int suffixLength = 16;
        //CSJ,variable=extension,拡張子
        var extension = Path.GetExtension(filename);
        var stem = Path.GetFileNameWithoutExtension(filename);
        //CSJ,variable=length,長さ
        var length = Math.Min(stem.Length, 255 - extension.Length - suffixLength - 1);
        if (length < 0) throw new InvalidDataException("配布用の名前を付加できない長さの拡張子です。");
        if (length > 0 && char.IsHighSurrogate(stem[length - 1])) length--;
        var result = stem[..length] + "_" + sha256[..suffixLength].ToUpperInvariant() + extension;
        if (!AssetInventory.IsOrdinaryPathComponent(result)) throw new InvalidDataException("配布用の名前が無効です。");
        return result;
    }
}
