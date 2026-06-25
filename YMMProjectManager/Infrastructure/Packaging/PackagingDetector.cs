using System.IO;
using System.Text.Json.Nodes;

namespace YMMProjectManager.Infrastructure.Packaging;

public static class PackagingDetector
{
    public static IReadOnlyList<string> GetProjectFilePaths(string projectPath)
    {
        if (!File.Exists(projectPath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(projectPath);
            return FindFilePaths(json);
        }
        catch
        {
            return [];
        }
    }

    public static IReadOnlyList<string> FindFilePaths(string projectJson)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(projectJson);
        }
        catch
        {
            return [];
        }

        if (root is null)
        {
            return [];
        }

        var result = new List<string>();
        CollectFilePaths(root, result);
        return result;
    }

    public static string? ResolveMaterialPath(string filePath, string projectDir)
    {
        if (!TryNormalizeInputPath(filePath, out var normalizedPath))
        {
            return null;
        }

        if (Path.IsPathRooted(normalizedPath))
        {
            try
            {
                return Path.GetFullPath(normalizedPath);
            }
            catch
            {
                return null;
            }
        }

        try
        {
            return Path.GetFullPath(Path.Combine(projectDir, normalizedPath));
        }
        catch
        {
            return null;
        }
    }

    public static string NormalizeFilePathKey(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return string.Empty;
        }

        var trimmed = filePath.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            trimmed = uri.LocalPath;
        }

        return trimmed.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private static void CollectFilePaths(JsonNode node, IList<string> result)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                if (obj.TryGetPropertyValue("FilePath", out var filePathNode) &&
                    filePathNode is JsonValue filePathValue &&
                    filePathValue.TryGetValue<string>(out var pathValue) &&
                    !string.IsNullOrWhiteSpace(pathValue))
                {
                    result.Add(pathValue);
                }

                foreach (var child in obj)
                {
                    if (child.Value is not null)
                    {
                        CollectFilePaths(child.Value, result);
                    }
                }

                break;
            }
            case JsonArray array:
            {
                foreach (var child in array)
                {
                    if (child is not null)
                    {
                        CollectFilePaths(child, result);
                    }
                }

                break;
            }
        }
    }

    private static bool TryNormalizeInputPath(string path, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            trimmed = uri.LocalPath;
        }

        normalized = trimmed.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return true;
    }
}
