using System.IO;

namespace YMMProjectManager.Infrastructure.Git;

internal static class GitPullWindowsTree
{
    private static readonly char[] InvalidNameCharacters = Path.GetInvalidFileNameChars();

    /// <summary>Checks NUL-delimited git ls-tree output, including directory entries from -r -t.</summary>
    internal static bool CanApply(string entries)
    {
        // Git can report a successful fast-forward even when two case-distinct tree entries
        // occupy one file in the Windows worktree. Check directories as well as leaf names.
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remaining = entries.AsSpan();
        while (!remaining.IsEmpty)
        {
            var end = remaining.IndexOf('\0');
            if (end < 0) return false;
            var entry = remaining[..end];
            remaining = remaining[(end + 1)..];
            var separator = entry.IndexOf('\t');
            if (separator < 7 || entry[6] != ' ' ||
                entry[..6] is not ("040000" or "100644" or "100755"))
                return false;

            var path = entry[(separator + 1)..];
            if (path.IsEmpty || !paths.Add(path.ToString())) return false;
            foreach (var component in path.Split('/'))
            {
                var name = path[component];
                if (!IsOrdinaryWindowsName(name)) return false;
            }
        }
        return true;
    }

    private static bool IsOrdinaryWindowsName(ReadOnlySpan<char> name)
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
