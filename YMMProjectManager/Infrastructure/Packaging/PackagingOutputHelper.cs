using System.IO;
using System.Text.RegularExpressions;

namespace YMMProjectManager.Infrastructure.Packaging;

public static class PackagingOutputHelper
{
    public static string GetStableAvailableFilePath(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        if (!Directory.Exists(directory))
        {
            return path;
        }

        var existingIndex = 0;
        var pattern = $"^{Regex.Escape(fileName)}_(\\d+){Regex.Escape(extension)}$";
        foreach (var existing in Directory.EnumerateFiles(directory))
        {
            var candidateName = Path.GetFileName(existing);
            if (!candidateName.StartsWith($"{fileName}_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(Path.GetExtension(candidateName), extension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = Regex.Match(candidateName, pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var index))
            {
                existingIndex = Math.Max(existingIndex, index);
            }
        }

        return Path.Combine(directory, $"{fileName}_{existingIndex + 1:D3}{extension}");
    }

    public static string GetStableAvailableDirectoryPath(string path)
    {
        if (!Directory.Exists(path))
        {
            return path;
        }

        var parent = Path.GetDirectoryName(path) ?? string.Empty;
        var leaf = Path.GetFileName(path);
        var index = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(parent, $"{leaf}_{index:D3}");
            index++;
        }
        while (Directory.Exists(candidate));

        return candidate;
    }

    public static string CreateTemporaryPackagePath(string finalPath)
    {
        var directory = Path.GetDirectoryName(finalPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(finalPath);
        var extension = Path.GetExtension(finalPath);
        return Path.Combine(directory, $".{name}.{Guid.NewGuid():N}.tmp{extension}");
    }

    public static void MoveGeneratedPackage(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("一時出力ファイルが見つかりません。", sourcePath);
        }

        File.Move(sourcePath, destinationPath, true);
    }

    public static string EnsureTrailingDirectorySeparator(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return Path.DirectorySeparatorChar.ToString();
        }

        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
