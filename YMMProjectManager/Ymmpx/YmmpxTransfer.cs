using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using YMMProjectManager.Domain.Packaging;
using static YMMProjectManager.Infrastructure.OwnedFilePublication;

namespace YMMProjectManager.Infrastructure.Packaging;

//CSJ,class=YmmpxTransfer,YMMPXパッケージの受け渡し
internal static class YmmpxTransfer
{
    //CSJ,field=ManifestName,目録名前
    internal const string ManifestName = "ymmpm-transfer.json";
    internal const string EvidenceProperty = "YMMProjectManagerTransferV2";
    internal const string LibraryMediaPrefix = "resources/ymmpm-asset-";
    internal const string ArchiveCommentPrefix = "YMMProjectManager transfer v1:";
    //CSJ,field=MediaPrefix,メディア接頭辞
    internal const string MediaPrefix = "resources/ymmpm-transfer/";
    //CSJ,field=MissingPrefix,欠落接頭辞
    internal const string MissingPrefix = "ymmpm-missing/";
    //CSJ,field=MaxManifestBytes,最大目録バイト列
    internal const int MaxManifestBytes = 16 * 1024 * 1024;
    //CSJ,field=MaxAssets,最大素材一覧
    private const int MaxAssets = 10000 - 3;
    //CSJ,field=MaxPackageBytes,最大パッケージバイト列
    private const long MaxPackageBytes = 100L * 1024 * 1024 * 1024;

    //CSJ,field=JsonOptions,JSONオプション
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    //CSJ,record=Manifest,目録
    internal sealed record Manifest(int Version, string PackageName, string PackageSha256, Asset[] Assets, MissingAsset[] Missing);
    //CSJ,record=Asset,素材
    internal sealed record Asset(string ArchivePath, string OriginalFileName, long Length, string Sha256, YmmpxTransferPermission Permission);
    //CSJ,record=MissingAsset,欠落素材
    internal sealed record MissingAsset(string ReferencePath, string OriginalFileName, long? Length, string? Sha256,
        YmmpxTransferPermission Permission);
    //CSJ,record=ManifestContent,目録内容
    private sealed record ManifestContent(string PackageName, IReadOnlyList<Asset> Assets, IReadOnlyList<MissingAsset> Missing);
    //CSJ,method=ReadManifestAsync,目録を読み取る（非同期）
    internal static async Task<Manifest> ReadManifestAsync(Stream stream, string packageName, CancellationToken token)
    {
        if (stream.Length is <= 0 or > MaxManifestBytes) throw new InvalidDataException("受け渡し情報のサイズが不正です。");
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        RejectDuplicateProperties(json.RootElement, token);
        //CSJ,variable=manifest,目録
        var manifest = json.RootElement.Deserialize<Manifest>(JsonOptions);
        if (manifest is not { Version: 1 or 2, Assets: not null, Missing: not null } ||
            !string.Equals(manifest.PackageName, packageName, StringComparison.OrdinalIgnoreCase) ||
            !IsSha256(manifest.PackageSha256) || (long)manifest.Assets.Length + manifest.Missing.Length > MaxAssets)
            throw new InvalidDataException("受け渡し情報がパッケージに対応しません。");

        //CSJ,variable=names,名前一覧
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        //CSJ,variable=asset,素材
        foreach (var asset in manifest.Assets)
        {
            token.ThrowIfCancellationRequested();
            if (asset is null || !(manifest.Version == 1
                ? IsTransferReference(asset.ArchivePath, MediaPrefix, asset.OriginalFileName)
                : IsLibraryMember(asset.ArchivePath, asset.OriginalFileName)) ||
                !IsApproved(asset.Permission) || asset.Length is < 0 or > MaxPackageBytes || !IsSha256(asset.Sha256) ||
                !names.Add(asset.ArchivePath))
                throw new InvalidDataException("受け渡し素材の情報が不正です。");
        }
        //CSJ,variable=missing,欠落
        foreach (var missing in manifest.Missing)
        {
            token.ThrowIfCancellationRequested();
            if (missing is null || !IsTransferReference(missing.ReferencePath, MissingPrefix, missing.OriginalFileName) ||
                !Enum.IsDefined(missing.Permission) || IsApproved(missing.Permission) ||
                (missing.Length.HasValue != (missing.Sha256 is not null)) ||
                (missing.Length.HasValue && (missing.Length is < 0 or > MaxPackageBytes || !IsSha256(missing.Sha256))) ||
                !names.Add(missing.ReferencePath))
                throw new InvalidDataException("不足素材の情報が不正です。");
        }
        return manifest;
    }

    internal static string MakeArchiveComment(string packageName, IReadOnlyList<Asset> assets, IReadOnlyList<MissingAsset> missing)
        => ArchiveCommentPrefix + ManifestDigest(packageName, assets, missing);

    internal static string ManifestDigest(string packageName, IReadOnlyList<Asset> assets, IReadOnlyList<MissingAsset> missing)
    {
        //CSJ,variable=content,内容
        var content = JsonSerializer.SerializeToUtf8Bytes(new ManifestContent(packageName, assets, missing), JsonOptions);
        return Convert.ToHexString(SHA256.HashData(content));
    }

    //CSJ,method=IsReferenceProperty,参照プロパティかを判定する
    internal static bool IsReferenceProperty(string name) =>
        name.Equals("FilePath", StringComparison.OrdinalIgnoreCase) || name.Equals("EnableLayersFilePath", StringComparison.OrdinalIgnoreCase);

    //CSJ,method=OpenOrdinaryRead,通常読み取りを開く
    internal static FileStream OpenOrdinaryRead(string path)
    {
        var handle = OpenOrdinaryPath(path, expectDirectory: false, allowRename: false);
        try { return new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }

    //CSJ,method=IsApproved,承認済みかを判定する
    private static bool IsApproved(YmmpxTransferPermission permission) =>
        permission is YmmpxTransferPermission.RedistributionAllowed or YmmpxTransferPermission.OwnWork;

    //CSJ,method=IsSha256,SHA-256かを判定する
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    //CSJ,method=IsTransferReference,受け渡し参照かを判定する
    private static bool IsTransferReference(string? path, string prefix, string? name)
    {
        if (path is null || !path.StartsWith(prefix, StringComparison.Ordinal) || !IsPortableName(name)) return false;
        //CSJ,variable=parts,構成要素
        var parts = path[prefix.Length..].Split('/');
        return parts.Length == 2 && parts[0].Length == 8 && parts[0].All(Uri.IsHexDigit) &&
            string.Equals(parts[1], name, StringComparison.Ordinal);
    }

    internal static bool IsPortableName(string? name) => !string.IsNullOrWhiteSpace(name) &&
        AssetInventory.IsOrdinaryPathComponent(name) && !name.Any(char.IsControl);

    //CSJ,method=ReadProjectAsync,プロジェクトを読み取る（非同期）
    internal static async Task<(JsonObject Project, string Sha256)> ReadProjectAsync(FileStream stream, string path, CancellationToken token)
    {
        if (!Path.GetExtension(path).Equals(".ymmp", StringComparison.OrdinalIgnoreCase) ||
            stream.Length is <= 0 or > YmmpxPackageReader.MaxProjectBytes)
            throw new InvalidDataException("有効な .ymmp プロジェクトを指定してください。");
        //CSJ,variable=bytes,バイト列
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        //CSJ,variable=text,テキスト
        var text = DecodeProject(bytes);
        //CSJ,variable=document,文書
        using var document = JsonDocument.Parse(text);
        RejectDuplicateProperties(document.RootElement, token);
        return (JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("プロジェクト JSON が不正です。"),
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static string DecodeProject(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) return new UTF32Encoding(false, false, true).GetString(bytes[4..]);
        if (bytes.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) return new UTF32Encoding(true, false, true).GetString(bytes[4..]);
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE })) return new UnicodeEncoding(false, false, true).GetString(bytes[2..]);
        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF })) return new UnicodeEncoding(true, false, true).GetString(bytes[2..]);
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) bytes = bytes[3..];
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    //CSJ,method=RejectDuplicateProperties,重複プロパティ一覧を拒否する
    private static void RejectDuplicateProperties(JsonElement node, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (node.ValueKind == JsonValueKind.Object)
        {
            //CSJ,variable=names,名前一覧
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            //CSJ,variable=property,プロパティ
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("JSON に重複した項目名があります。");
                RejectDuplicateProperties(property.Value, token);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray()) RejectDuplicateProperties(child, token);
    }

    internal static bool IsLibraryMemberName(string name)
    {
        const string prefix = "ymmpm-asset-";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !IsPortableName(name)) return false;
        var stem = Path.GetFileNameWithoutExtension(name);
        return stem.Length == prefix.Length + 13 && stem.EndsWith("-file", StringComparison.OrdinalIgnoreCase) &&
            stem.Substring(prefix.Length, 8).All(Uri.IsHexDigit);
    }

    private static bool IsLibraryMember(string? member, string? originalName)
    {
        if (!IsPortableName(originalName) || member is null || !member.StartsWith(LibraryMediaPrefix, StringComparison.Ordinal))
            return false;
        var tail = member[LibraryMediaPrefix.Length..];
        return tail.Length >= 13 && tail[..8].All(Uri.IsHexDigit) &&
            tail[8..] == "-file" + Path.GetExtension(originalName);
    }
}
