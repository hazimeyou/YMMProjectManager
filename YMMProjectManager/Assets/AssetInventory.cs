using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using YMMProjectManager.Infrastructure.Packaging;

namespace YMMProjectManager.Infrastructure;

//CSJ,record=ExternalAssetLocation,外部素材場所
public sealed record ExternalAssetLocation(string StorageName, string RelativePath);
//CSJ,record=ExternalRelinkBinding,外部素材の再リンク照合情報
public sealed record ExternalRelinkBinding(string OriginalFileName, string ReferenceSha256, string? ManifestSha256);
//CSJ,record=AssetMetadata,素材メタデータ
public sealed record AssetMetadata(string RelativePath, long Size, string Sha256, string? DownloadUrl)
{
    //CSJ,property=ExternalLocation,外部場所
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExternalAssetLocation? ExternalLocation { get; init; }
    //CSJ,property=ExternalRelink,外部再リンク
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExternalRelinkBinding? ExternalRelink { get; init; }
}
//CSJ,enum=AssetVerification,素材検証
public enum AssetVerification { Valid, Missing, SizeMismatch, HashMismatch, ExternalUnverified, Unreadable }
//CSJ,enum=AssetReferenceStatus,素材参照状態
public enum AssetReferenceStatus { Present, Missing, Changed, External, Unavailable }
//CSJ,record=AssetReference,素材参照
public sealed record AssetReference(string Path, AssetReferenceStatus Status, AssetMetadata? Actual, AssetMetadata? Expected);

/// <summary>Inspects explicitly selected local assets; it never modifies media files.</summary>
public static class AssetInventory
{
    public static async Task<IReadOnlyList<AssetReference>> ScanProjectAsync(string projectPath,
        IReadOnlyList<AssetMetadata>? expected = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        //CSJ,variable=project,プロジェクト
        var project = Path.GetFullPath(projectPath);
        //CSJ,variable=directory,ディレクトリ
        var directory = Path.GetDirectoryName(project) ?? throw new InvalidDataException("The project has no containing directory.");
        //CSJ,variable=safeProject,安全プロジェクト
        var safeProject = ResolveRelativePath(directory, Path.GetFileName(project));
        //CSJ,variable=fence,整合性境界
        using var fence = OwnedFilePublication.DirectoryFence.Open(directory, enforceShare: true);
        //CSJ,variable=stream,ストリーム
        await using var stream = OpenOrdinaryAsset(safeProject);
        if (stream.Length > 32 * 1024 * 1024) throw new InvalidDataException("The project is too large for inventory scanning.");
        //CSJ,variable=reader,読み取り元
        using var reader = new StreamReader(stream);
        var json = await reader.ReadToEndAsync(token).ConfigureAwait(false);
        var expectedByPath = new Dictionary<string, AssetMetadata>(StringComparer.OrdinalIgnoreCase);
        //CSJ,variable=expectedExternal,期待値外部
        var expectedExternal = new Dictionary<string, AssetMetadata>(StringComparer.OrdinalIgnoreCase);
        //CSJ,variable=asset,素材
        foreach (var asset in expected ?? [])
        {
            ValidateMetadata(asset);
            if (!expectedByPath.TryAdd(Path.GetFullPath(asset.RelativePath, directory), asset))
                throw new InvalidDataException("Duplicate expected asset path.");
            if (asset.ExternalRelink is { } binding && !expectedExternal.TryAdd(binding.ReferenceSha256, asset))
                throw new InvalidDataException("Duplicate external relink asset reference.");
        }

        //CSJ,variable=projectJson,プロジェクトJSON
        using var projectJson = JsonDocument.Parse(json);
        if (projectJson.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The project does not contain a JSON object.");
        //CSJ,variable=paths,パス一覧
        var paths = PackagingDetector.FindFilePaths(json);
        if (paths.Count > 1000) throw new InvalidDataException("The project has too many asset references for one inventory scan.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        //CSJ,variable=results,結果一覧
        var results = new List<AssetReference>();
        //CSJ,variable=prefix,接頭辞
        var prefix = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        //CSJ,variable=reference,参照
        foreach (var reference in paths)
        {
            token.ThrowIfCancellationRequested();
            //CSJ,variable=path,パス
            var path = PackagingDetector.ResolveMaterialPath(reference, directory);
            if (!seen.Add(path ?? reference)) continue;
            if (path is null)
            {
                results.Add(new AssetReference(reference, AssetReferenceStatus.Unavailable, null, null));
                continue;
            }
            if (expectedExternal.TryGetValue(StrictRelinkAssets.ReferenceSha256(path), out var relocated))
            {
                results.Add(new AssetReference(path, AssetReferenceStatus.External, null, relocated));
                continue;
            }
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new AssetReference(path, AssetReferenceStatus.External, null, null));
                continue;
            }
            //CSJ,variable=relative,相対
            var relative = Path.GetRelativePath(directory, path);
            expectedByPath.TryGetValue(path, out var prior);
            if (prior?.ExternalLocation is not null)
            {
                results.Add(new AssetReference(relative, AssetReferenceStatus.External, null, prior));
                continue;
            }
            try
            {
                var actual = await InspectAsync(directory, relative, prior?.DownloadUrl, token).ConfigureAwait(false);
                //CSJ,variable=changed,変更済み
                var changed = prior is not null && (actual.Size != prior.Size ||
                    !string.Equals(actual.Sha256, prior.Sha256, StringComparison.OrdinalIgnoreCase));
                results.Add(new AssetReference(relative, changed ? AssetReferenceStatus.Changed : AssetReferenceStatus.Present, actual, prior));
            }
            catch (IOException ex) when (IsMissing(ex))
            {
                results.Add(new AssetReference(relative, AssetReferenceStatus.Missing, null, prior));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                results.Add(new AssetReference(relative, AssetReferenceStatus.Unavailable, null, prior));
            }
        }
        return results;
    }

    //CSJ,method=InspectAsync,点検する（非同期）
    public static async Task<AssetMetadata> InspectAsync(string projectDirectory, string relativePath, string? downloadUrl = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        //CSJ,variable=path,パス
        var path = ResolveRelativePath(projectDirectory, relativePath);
        ValidateUrl(downloadUrl);
        //CSJ,variable=fence,整合性境界
        using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(path)!, enforceShare: true);
        //CSJ,variable=stream,ストリーム
        await using var stream = OpenOrdinaryAsset(path);
        //CSJ,variable=size,サイズ
        var size = stream.Length;
        //CSJ,variable=hash,ハッシュ
        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        if (stream.Length != size) throw new IOException("Asset changed while being read.");
        return new AssetMetadata(relativePath, size, Convert.ToHexString(hash), downloadUrl);
    }

    //CSJ,method=VerifyAsync,検証する（非同期）
    public static async Task<AssetVerification> VerifyAsync(string projectDirectory, AssetMetadata asset, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ValidateMetadata(asset);
        if (asset.ExternalLocation is not null) return AssetVerification.ExternalUnverified;
        return await VerifyFileAsync(projectDirectory, asset.RelativePath, asset, token).ConfigureAwait(false);
    }

    //CSJ,method=InspectExternalAsync,外部を点検する（非同期）
    public static async Task<AssetMetadata> InspectExternalAsync(string approvedStorageRoot, string projectRelativePath,
        //CSJ,parameter=location,場所
        ExternalAssetLocation location, string? downloadUrl = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ValidateExternalLocation(location);
        //CSJ,variable=asset,素材
        var asset = await InspectAsync(approvedStorageRoot, location.RelativePath, downloadUrl, token).ConfigureAwait(false);
        var result = asset with { RelativePath = projectRelativePath, ExternalLocation = location };
        ValidateMetadata(result);
        return result;
    }

    //CSJ,method=VerifyExternalAsync,外部を検証する（非同期）
    public static async Task<AssetVerification> VerifyExternalAsync(string approvedStorageRoot, AssetMetadata asset, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ValidateMetadata(asset);
        if (asset.ExternalLocation is not { } location) throw new InvalidDataException("No external storage location is recorded.");
        return await VerifyFileAsync(approvedStorageRoot, location.RelativePath, asset, token).ConfigureAwait(false);
    }

    //CSJ,method=VerifyExternalBatchAsync,外部一括処理を検証する（非同期）
    internal static Task<IReadOnlyList<AssetVerification>> VerifyExternalBatchAsync(
        //CSJ,parameter=approvedStorageRoot,承認済み保管ルート
        string approvedStorageRoot, IReadOnlyList<AssetMetadata> assets, CancellationToken token)
    {
        // Path inspection performs synchronous filesystem calls before the asynchronous file reads.
        return Task.Run<IReadOnlyList<AssetVerification>>(async () =>
        {
            //CSJ,variable=results,結果一覧
            var results = new AssetVerification[assets.Count];
            //CSJ,variable=index,インデックス
            for (var index = 0; index < assets.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    results[index] = await VerifyExternalAsync(approvedStorageRoot, assets[index], token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    results[index] = AssetVerification.Unreadable;
                }
            }
            token.ThrowIfCancellationRequested();
            return results;
        }, token);
    }

    //CSJ,method=VerifyFileAsync,ファイルを検証する（非同期）
    private static async Task<AssetVerification> VerifyFileAsync(
        //CSJ,parameter=directory,ディレクトリ
        string directory, string relativePath, AssetMetadata asset, CancellationToken token)
    {
        try
        {
            //CSJ,variable=path,パス
            var path = ResolveRelativePath(directory, relativePath);
            //CSJ,variable=fence,整合性境界
            using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(path)!, enforceShare: true);
            //CSJ,variable=stream,ストリーム
            await using var stream = OpenOrdinaryAsset(path);
            if (stream.Length != asset.Size) return AssetVerification.SizeMismatch;
            //CSJ,variable=hash,ハッシュ
            var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
            if (stream.Length != asset.Size) return AssetVerification.SizeMismatch;
            return string.Equals(Convert.ToHexString(hash), asset.Sha256, StringComparison.OrdinalIgnoreCase) ? AssetVerification.Valid : AssetVerification.HashMismatch;
        }
        catch (IOException ex) when (IsMissing(ex))
        { return AssetVerification.Missing; }
    }

    //CSJ,method=IsMissing,欠落かを判定する
    private static bool IsMissing(IOException exception) =>
        exception is FileNotFoundException or DirectoryNotFoundException ||
        exception.InnerException is Win32Exception { NativeErrorCode: 2 or 3 };

    //CSJ,method=OpenOrdinaryAsset,通常素材を開く
    private static FileStream OpenOrdinaryAsset(string path)
    {
        try
        {
            var handle = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename: false);
            try { return new FileStream(handle, FileAccess.Read); }
            catch { handle.Dispose(); throw; }
        }
        catch (IOException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 2 or 3 })
        {
            throw new FileNotFoundException("Asset file was not found.", path, ex);
        }
    }

    // Export is deliberately create-only: an existing manifest, including a corrupt one, is never overwritten.
    //CSJ,method=ExportAsync,書き出す（非同期）
    public static async Task ExportAsync(string path, IReadOnlyCollection<AssetMetadata> assets, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(assets);
        token.ThrowIfCancellationRequested();
        //CSJ,variable=asset,素材
        foreach (var asset in assets) ValidateMetadata(asset);
        //CSJ,variable=bytes,バイト列
        var bytes = JsonSerializer.SerializeToUtf8Bytes(assets, new JsonSerializerOptions { WriteIndented = true });
        //CSJ,variable=fullPath,完全パス
        var fullPath = Path.GetFullPath(path);
        //CSJ,variable=temporary,一時
        var temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        //CSJ,variable=fence,整合性境界
        using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(fullPath)!, enforceShare: true);
        //CSJ,variable=stream,ストリーム
        await using var stream = OwnedFilePublication.CreateOwnedOutputFile(temporary);
        //CSJ,variable=published,確定済み
        var published = false;
        try
        {
            await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            token.ThrowIfCancellationRequested();
            OwnedFilePublication.RenameOpenedPath(stream.SafeFileHandle, fullPath);
            published = true;
        }
        finally
        {
            if (!published) OwnedFilePublication.TryDeleteOpenedPath(stream.SafeFileHandle);
        }
    }

    //CSJ,method=LoadAsync,読み込む（非同期）
    public static async Task<IReadOnlyList<AssetMetadata>> LoadAsync(string path, CancellationToken token = default)
    {
        //CSJ,variable=stream,ストリーム
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Asset metadata is too large.");
        //CSJ,variable=assets,素材一覧
        var assets = await JsonSerializer.DeserializeAsync<List<AssetMetadata>>(stream, cancellationToken: token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Asset metadata is empty.");
        //CSJ,variable=asset,素材
        foreach (var asset in assets) ValidateMetadata(asset);
        return assets;
    }

    //CSJ,method=ValidateMetadata,メタデータを検証する
    internal static void ValidateMetadata(AssetMetadata asset)
    {
        if (asset is null || string.IsNullOrWhiteSpace(asset.RelativePath) || Path.IsPathRooted(asset.RelativePath) ||
            asset.RelativePath.Split('/', '\\').Any(part => part == "..") || asset.Size < 0 ||
            asset.Sha256 is null || asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid asset metadata.");
        ValidateUrl(asset.DownloadUrl);
        if (asset.ExternalLocation is { } location) ValidateExternalLocation(location);
        if (asset.ExternalRelink is { } binding && (asset.ExternalLocation is null ||
            binding.OriginalFileName is null || !IsOrdinaryPathComponent(binding.OriginalFileName) ||
            !IsFullSha256(binding.ReferenceSha256) || (binding.ManifestSha256 is not null && !IsFullSha256(binding.ManifestSha256))))
            throw new InvalidDataException("Invalid external relink asset binding.");
    }

    //CSJ,method=IsFullSha256,完全SHA-256かを判定する
    internal static bool IsFullSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    //CSJ,method=ValidateExternalLocation,外部場所を検証する
    private static void ValidateExternalLocation(ExternalAssetLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (string.IsNullOrWhiteSpace(location.StorageName) || location.StorageName.Length > 128 ||
            location.StorageName.Any(character => char.IsControl(character) || character is '/' or '\\' or ':') ||
            string.IsNullOrWhiteSpace(location.RelativePath) || Path.IsPathRooted(location.RelativePath) ||
            location.RelativePath.Split('/', '\\').Any(part => part is "" or "." or ".." ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Invalid external asset location.");
    }
    //CSJ,method=ResolveRelativePath,相対パスを解決する
    internal static string ResolveRelativePath(string projectDirectory, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath)) throw new ArgumentException("Asset path must be relative.", nameof(relativePath));
        //CSJ,variable=root,ルート
        var root = Path.GetFullPath(projectDirectory);
        //CSJ,variable=full,絶対パス
        var full = Path.GetFullPath(relativePath, root);
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Asset path escapes the project directory.", nameof(relativePath));
        for (var ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
            if ((File.GetAttributes(ancestor.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Project path cannot traverse a reparse point.");
        var current = root;
        //CSJ,variable=segment,区間
        foreach (var segment in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (!IsOrdinaryPathComponent(segment))
                throw new ArgumentException("Asset path contains an invalid or reserved name.", nameof(relativePath));
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Asset paths through reparse points are not supported.");
        }
        return full;
    }

    //CSJ,method=IsOrdinaryPathComponent,通常パス構成要素かを判定する
    internal static bool IsOrdinaryPathComponent(string name)
    {
        var stem = name.Split('.')[0].TrimEnd();
        return name.Length is > 0 and <= 255 &&
            !name.EndsWith('.') && !name.EndsWith(' ') && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
            !stem.Equals("CON", StringComparison.OrdinalIgnoreCase) && !stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) &&
            !stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) && !stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) &&
            !stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) && !stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) &&
            !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && "0123456789¹²³".Contains(stem[3]));
    }

    //CSJ,method=ValidateUrl,URLを検証する
    internal static void ValidateUrl(string? value)
    {
        if (value is null) return;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)))
            throw new ArgumentException("Asset URL must be HTTPS without credentials, query, or fragment.", nameof(value));
    }
}
