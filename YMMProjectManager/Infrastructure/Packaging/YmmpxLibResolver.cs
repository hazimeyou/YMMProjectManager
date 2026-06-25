using System.IO;
using System.Reflection;

namespace YMMProjectManager.Infrastructure.Packaging;

public static class YmmpxLibResolver
{
    private const string LibraryFileName = "YmmpxLib.dll";
    private const string LibraryAssemblyName = "YmmpxLib";
    private const string PluginRelativePath = @"user\plugin\YmmpxLibPlugin\YmmpxLib.dll";

    public static IReadOnlyList<string> GetSearchPaths(string? baseDirectory = null, string? explicitAssemblyPath = null)
    {
        var candidates = new List<string>();

        AddCandidate(candidates, explicitAssemblyPath);
        AddCandidate(candidates, Environment.GetEnvironmentVariable("YMMPX_LIB_PATH"));

        var resolvedBaseDirectory = ResolveBaseDirectory(baseDirectory);
        if (!string.IsNullOrWhiteSpace(resolvedBaseDirectory))
        {
            AddCandidate(candidates, Path.Combine(resolvedBaseDirectory, LibraryFileName));
            AddCandidate(candidates, Path.Combine(resolvedBaseDirectory, "YmmpxLibPlugin", LibraryFileName));

            foreach (var ancestor in EnumerateAncestors(resolvedBaseDirectory))
            {
                AddCandidate(candidates, Path.Combine(ancestor, PluginRelativePath));
            }
        }

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path =>
            {
                try
                {
                    return Path.GetFullPath(path);
                }
                catch
                {
                    return string.Empty;
                }
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
    }

    public static bool TryResolveAssembly(
        out Assembly? assembly,
        out string? assemblyPath,
        out IReadOnlyList<string> searchedPaths,
        string? baseDirectory = null,
        string? explicitAssemblyPath = null)
    {
        searchedPaths = GetSearchPaths(baseDirectory, explicitAssemblyPath);

        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, LibraryAssemblyName, StringComparison.OrdinalIgnoreCase));
        if (loaded is not null)
        {
            assembly = loaded;
            assemblyPath = SafeGetLocation(loaded);
            return true;
        }

        foreach (var candidate in searchedPaths)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                assembly = Assembly.LoadFrom(candidate);
                assemblyPath = candidate;
                return true;
            }
            catch
            {
                // 次の候補へ進む。
            }
        }

        assembly = null;
        assemblyPath = null;
        return false;
    }

    private static void AddCandidate(ICollection<string> candidates, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        candidates.Add(path.Trim().Trim('"'));
    }

    private static string? ResolveBaseDirectory(string? baseDirectory)
    {
        var candidate = baseDirectory;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = AppContext.BaseDirectory;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(candidate);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateAncestors(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            yield return current.FullName;
            current = current.Parent;
        }
    }

    private static string? SafeGetLocation(Assembly assembly)
    {
        try
        {
            return string.IsNullOrWhiteSpace(assembly.Location) ? null : Path.GetFullPath(assembly.Location);
        }
        catch
        {
            return null;
        }
    }
}
