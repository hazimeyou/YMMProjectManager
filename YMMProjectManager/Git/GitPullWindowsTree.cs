using System.IO;

namespace YMMProjectManager.Infrastructure.Git;

//CSJ,class=GitPullWindowsTree,Gitプル時のWindowsパス検証
internal static class GitPullWindowsTree
{
    private static readonly char[] InvalidNameCharacters = Path.GetInvalidFileNameChars();

    /// <summary>Checks NUL-delimited git ls-tree output, including directory entries from -r -t.</summary>
    //CSJ,method=TryReadFilePaths,ファイルパス一覧の読み取りを試みる
    internal static bool TryReadFilePaths(string entries, out List<string> files)
    {
        files = [];
        // Git can report a successful fast-forward even when two case-distinct tree entries
        // occupy one file in the Windows worktree. Check directories as well as leaf names.
        //CSJ,variable=paths,パス一覧
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remaining = entries.AsSpan();
        while (!remaining.IsEmpty)
        {
            var end = remaining.IndexOf('\0');
            if (end < 0) return false;
            var entry = remaining[..end];
            remaining = remaining[(end + 1)..];
            //CSJ,variable=separator,区切り
            var separator = entry.IndexOf('\t');
            if (separator < 7 || entry[6] != ' ' ||
                entry[..6] is not ("040000" or "100644" or "100755"))
                return false;

            //CSJ,variable=path,パス
            var path = entry[(separator + 1)..];
            var pathString = path.ToString();
            if (path.IsEmpty || !paths.Add(pathString)) return false;
            //CSJ,variable=component,構成要素
            foreach (var component in path.Split('/'))
            {
                //CSJ,variable=name,名前
                var name = path[component];
                if (!IsOrdinaryWindowsName(name)) return false;
            }
            if (entry[..6] is not "040000") files.Add(pathString);
        }
        return true;
    }

    //CSJ,method=IsOrdinaryWindowsName,通常のWindowsファイル名として有効か判定する
    internal static bool IsOrdinaryWindowsName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || name.Length > 255 || name[^1] is ' ' or '.' ||
            name.Contains('\uFFFD') || name.IndexOfAny(InvalidNameCharacters) >= 0 ||
            name.Equals(".git", StringComparison.OrdinalIgnoreCase))
            return false;

        var dot = name.IndexOf('.');
        var stem = (dot < 0 ? name : name[..dot]).TrimEnd();
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
            return false;

        return stem.Length != 4 ||
            (!stem[..3].Equals("COM", StringComparison.OrdinalIgnoreCase) &&
             !stem[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase)) ||
            !"0123456789¹²³".Contains(stem[3]);
    }
}
