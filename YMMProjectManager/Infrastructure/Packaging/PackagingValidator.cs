using System.IO;
using System.Linq;

namespace YMMProjectManager.Infrastructure.Packaging;

public sealed class PackagingValidationResult
{
    public int DetectedMaterialCount { get; set; }

    public int MissingMaterialCount { get; set; }
}

public static class PackagingValidator
{
    public static PackagingValidationResult ValidateProjectBeforePack(string projectPath)
    {
        var projectDir = Path.GetDirectoryName(projectPath) ?? string.Empty;
        var detected = PackagingDetector.GetProjectFilePaths(projectPath);
        var missing = 0;

        foreach (var rawPath in detected.Distinct(StringComparer.OrdinalIgnoreCase))
        {
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
