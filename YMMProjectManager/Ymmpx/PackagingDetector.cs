using System.IO;
using System.Text.Json.Nodes;

namespace YMMProjectManager.Infrastructure.Packaging;

//CSJ,class=PackagingDetector,パッケージ形式の検出処理
public static class PackagingDetector
{
    //CSJ,method=GetProjectFilePaths,プロジェクトファイルパス一覧を取得する
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

    //CSJ,method=FindFilePaths,ファイルパス一覧を探す
    public static IReadOnlyList<string> FindFilePaths(string projectJson)
    {
        //CSJ,variable=root,ルート
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

    //CSJ,method=ResolveMaterialPath,素材パスを解決する
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

    //CSJ,method=NormalizeFilePathKey,ファイルパスキーを正規化する
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

    //CSJ,method=CollectFilePaths,ファイルパス一覧を収集する
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

    //CSJ,method=TryNormalizeInputPath,入力パスの正規化を試みる
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
