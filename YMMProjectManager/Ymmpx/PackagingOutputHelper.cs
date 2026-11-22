using System.IO;
using System.Text.RegularExpressions;

namespace YMMProjectManager.Infrastructure.Packaging;

//CSJ,class=PackagingOutputHelper,パッケージ化出力補助処理
public static class PackagingOutputHelper
{
    //CSJ,method=GetStableAvailableFilePath,安定利用可能ファイルパスを取得する
    public static string GetStableAvailableFilePath(string path)
    {
        //CSJ,variable=directory,ディレクトリ
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        //CSJ,variable=fileName,ファイル名
        var fileName = Path.GetFileNameWithoutExtension(path);
        //CSJ,variable=extension,拡張子
        var extension = Path.GetExtension(path);

        if (!Directory.Exists(directory))
        {
            return path;
        }

        //CSJ,variable=existingIndex,既存インデックス
        var existingIndex = 0;
        //CSJ,variable=pattern,パターン
        var pattern = $"^{Regex.Escape(fileName)}_(\\d+){Regex.Escape(extension)}$";
        //CSJ,variable=existing,既存
        foreach (var existing in Directory.EnumerateFiles(directory))
        {
            //CSJ,variable=candidateName,候補名前
            var candidateName = Path.GetFileName(existing);
            if (!candidateName.StartsWith($"{fileName}_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(Path.GetExtension(candidateName), extension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            //CSJ,variable=match,一致
            var match = Regex.Match(candidateName, pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var index))
            {
                existingIndex = Math.Max(existingIndex, index);
            }
        }

        return Path.Combine(directory, $"{fileName}_{existingIndex + 1:D3}{extension}");
    }

    //CSJ,method=GetStableAvailableDirectoryPath,安定利用可能ディレクトリのパスを取得する
    public static string GetStableAvailableDirectoryPath(string path)
    {
        if (!Directory.Exists(path))
        {
            return path;
        }

        var parent = Path.GetDirectoryName(path) ?? string.Empty;
        var leaf = Path.GetFileName(path);
        //CSJ,variable=index,インデックス
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

    //CSJ,method=CreateTemporaryPackagePath,一時パッケージのパスを作成する
    public static string CreateTemporaryPackagePath(string finalPath)
    {
        //CSJ,variable=directory,ディレクトリ
        var directory = Path.GetDirectoryName(finalPath) ?? string.Empty;
        //CSJ,variable=name,名前
        var name = Path.GetFileNameWithoutExtension(finalPath);
        //CSJ,variable=extension,拡張子
        var extension = Path.GetExtension(finalPath);
        return Path.Combine(directory, $".{name}.{Guid.NewGuid():N}.tmp{extension}");
    }

    //CSJ,method=MoveGeneratedPackage,生成したパッケージを移動する
    public static void MoveGeneratedPackage(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("一時出力ファイルが見つかりません。", sourcePath);
        }

        File.Move(sourcePath, destinationPath, overwrite: false);
    }

    //CSJ,method=EnsureTrailingDirectorySeparator,末尾のディレクトリ区切り文字を補う
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
