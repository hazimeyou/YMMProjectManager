using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YMMProjectManager.Infrastructure.Packaging;

namespace YMMProjectManager.Infrastructure;

/// <summary>Maps a file reference to trusted project metadata without accessing the referenced path.</summary>
//CSJ,class=StrictRelinkAssets,厳密照合の再リンク素材
internal sealed class StrictRelinkAssets
{
    //CSJ,field=directory,ディレクトリ
    private readonly string directory;
    private readonly string? declaredDirectory;
    //CSJ,field=expected,期待値
    private readonly Dictionary<string, AssetMetadata> expected;
    //CSJ,field=externalReferences,外部参照一覧
    private readonly Dictionary<string, AssetMetadata> externalReferences = new(StringComparer.OrdinalIgnoreCase);

    internal StrictRelinkAssets(string projectPath, string json, AssetManifest manifest)
    {
        directory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        //CSJ,variable=project,プロジェクト
        using var project = JsonDocument.Parse(json.TrimStart('\uFEFF'));
        //CSJ,variable=root,ルート
        var root = project.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("FilePath", out var path) &&
            path.ValueKind == JsonValueKind.String && path.GetString() is { } reference)
        {
            var value = PackagingDetector.NormalizeFilePathKey(reference);
            if (Path.IsPathFullyQualified(value) && Path.GetExtension(value).Equals(".ymmp", StringComparison.OrdinalIgnoreCase))
                declaredDirectory = Path.GetDirectoryName(Path.GetFullPath(value));
        }
        expected = manifest.Assets.ToDictionary(asset => Path.GetFullPath(asset.RelativePath, directory),
            StringComparer.OrdinalIgnoreCase);
        //CSJ,variable=asset,素材
        foreach (var asset in manifest.Assets)
            if (asset.ExternalRelink is { } binding && !externalReferences.TryAdd(binding.ReferenceSha256, asset))
                throw new InvalidDataException("外部素材の参照とメタデータの対応が重複しています。");
    }

    //CSJ,method=FindExpected,期待値を探す
    internal AssetMetadata? FindExpected(string originalReference)
    {
        var full = PackagingDetector.ResolveMaterialPath(originalReference, directory);
        if (full is null) return null;
        expected.TryGetValue(full, out var direct);
        externalReferences.TryGetValue(ReferenceSha256(full), out var external);
        AssetMetadata? transferred = null;
        if (declaredDirectory is not null && IsDescendant(declaredDirectory, full))
            expected.TryGetValue(Path.GetFullPath(Path.GetRelativePath(declaredDirectory, full), directory), out transferred);
        //CSJ,variable=match,一致
        var match = direct ?? transferred ?? external;
        if ((direct is not null && direct != match) || (transferred is not null && transferred != match) ||
            (external is not null && external != match))
            throw new InvalidDataException("元のPFの参照と移動先のメタデータが異なる素材を示しています。");
        return match;
    }

    //CSJ,method=ReferenceSha256,参照SHA-256
    internal static string ReferenceSha256(string canonicalPath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath)));

    internal static bool IsDescendant(string directory, string path)
    {
        //CSJ,variable=prefix,接頭辞
        var prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }
}
