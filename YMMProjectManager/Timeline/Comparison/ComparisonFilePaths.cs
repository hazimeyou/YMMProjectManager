using System.IO;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Application.PreviewComparison;

internal static class ComparisonFilePaths
{
    internal static string GetLocalFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Length < 4 ||
            !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/') ||
            path[3..].Split(['\\', '/']).Any(component => !AssetInventory.IsOrdinaryPathComponent(component)))
            throw new IOException("通常のローカルファイルのパスを指定してください。");

        var fullPath = Path.GetFullPath(path);
        if (new DriveInfo(Path.GetPathRoot(fullPath)!).DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new IOException("比較にはローカルドライブを使用してください。");
        return fullPath;
    }
}
