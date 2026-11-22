using System.IO;
using System.Reflection;

namespace YMMProjectManager.Infrastructure.Packaging;

//CSJ,class=YmmpxLibResolver,YMMPXライブラリ解決処理
public static class YmmpxLibResolver
{
    //CSJ,field=LibraryFileName,ライブラリファイル名
    private const string LibraryFileName = "YmmpxLib.dll";
    //CSJ,field=LibraryAssemblyName,ライブラリアセンブリ名前
    private const string LibraryAssemblyName = "YmmpxLib";
    //CSJ,field=PluginRelativePath,プラグイン相対パス
    private const string PluginRelativePath = @"user\plugin\YmmpxLibPlugin\YmmpxLib.dll";

    //CSJ,method=GetSearchPaths,検索パス一覧を取得する
    public static IReadOnlyList<string> GetSearchPaths(string? baseDirectory = null, string? explicitAssemblyPath = null)
    {
        //CSJ,variable=candidates,候補一覧
        var candidates = new List<string>();

        AddCandidate(candidates, explicitAssemblyPath);
        AddCandidate(candidates, Environment.GetEnvironmentVariable("YMMPX_LIB_PATH"));

        //CSJ,variable=resolvedBaseDirectory,解決済み基点ディレクトリ
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

    //CSJ,method=TryResolveAssembly,アセンブリの解決を試みる
    public static bool TryResolveAssembly(
        //CSJ,parameter=assembly,アセンブリ
        out Assembly? assembly,
        //CSJ,parameter=assemblyPath,アセンブリのパス
        out string? assemblyPath,
        out IReadOnlyList<string> searchedPaths,
        //CSJ,parameter=baseDirectory,基点ディレクトリ
        string? baseDirectory = null,
        string? explicitAssemblyPath = null)
    {
        searchedPaths = GetSearchPaths(baseDirectory, explicitAssemblyPath);

        //CSJ,variable=loaded,読み込み済み
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

    //CSJ,method=AddCandidate,候補を追加する
    private static void AddCandidate(ICollection<string> candidates, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        candidates.Add(path.Trim().Trim('"'));
    }

    //CSJ,method=ResolveBaseDirectory,基点ディレクトリを解決する
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

    //CSJ,method=SafeGetLocation,安全取得場所
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
