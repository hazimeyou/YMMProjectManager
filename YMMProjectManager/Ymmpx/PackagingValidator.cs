using System.IO;
using System.Linq;

namespace YMMProjectManager.Infrastructure.Packaging;

//CSJ,class=PackagingValidationResult,パッケージ化妥当性検証結果
public sealed class PackagingValidationResult
{
    //CSJ,property=DetectedMaterialCount,検出した素材数
    public int DetectedMaterialCount { get; set; }

    //CSJ,property=MissingMaterialCount,欠落素材件数
    public int MissingMaterialCount { get; set; }
}

//CSJ,class=PackagingValidator,パッケージの検証処理
public static class PackagingValidator
{
    //CSJ,method=ValidateProjectBeforePack,パッケージ作成前にプロジェクトを検証する
    public static PackagingValidationResult ValidateProjectBeforePack(string projectPath)
    {
        var projectDir = Path.GetDirectoryName(projectPath) ?? string.Empty;
        var detected = PackagingDetector.GetProjectFilePaths(projectPath);
        //CSJ,variable=missing,欠落
        var missing = 0;

        //CSJ,variable=rawPath,生データパス
        foreach (var rawPath in detected.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            //CSJ,variable=resolved,解決済み
            var resolved = PackagingDetector.ResolveMaterialPath(rawPath, projectDir);
            if (resolved is null || !File.Exists(resolved))
            {
                missing++;
            }
        }

        return new PackagingValidationResult
        {
            DetectedMaterialCount = detected.Count,
            MissingMaterialCount = missing,
        };
    }
}
