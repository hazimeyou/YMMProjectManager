namespace YMMProjectManager.Presentation.Timeline;

public sealed class RuntimeEnvironmentDetector
{
    public RuntimeEnvironmentKind Detect(IReadOnlyList<string>? loadedAssemblyNames = null, string? processName = null)
    {
        var assemblies = loadedAssemblyNames ?? AppDomain.CurrentDomain.GetAssemblies()
            .Select(x => x.GetName().Name ?? string.Empty)
            .ToArray();
        var process = processName ?? SafeGetProcessName();

        if (assemblies.Any(x => x.Contains("YMMProjectManager.Benchmarks", StringComparison.OrdinalIgnoreCase)))
        {
            return RuntimeEnvironmentKind.Benchmark;
        }

        if (assemblies.Any(IsYmmAssemblyName) || process.Contains("YukkuriMovieMaker", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeEnvironmentKind.YMM4Plugin;
        }

        if (assemblies.Any(x => x.Contains("YMMProjectManager", StringComparison.OrdinalIgnoreCase)))
        {
            return RuntimeEnvironmentKind.Standalone;
        }

        return RuntimeEnvironmentKind.Unknown;
    }

    public string GetProcessName()
    {
        return SafeGetProcessName();
    }

    public IReadOnlyList<string> GetYmmRelatedAssemblyNames(IReadOnlyList<string> loadedAssemblyNames)
    {
        return loadedAssemblyNames.Where(IsYmmAssemblyName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> GetCandidateAssemblyNames(IReadOnlyList<string> loadedAssemblyNames)
    {
        return loadedAssemblyNames
            .Where(x =>
                x.Contains("Yukkuri", StringComparison.OrdinalIgnoreCase) ||
                x.Contains("YMM", StringComparison.OrdinalIgnoreCase) ||
                x.Contains("Timeline", StringComparison.OrdinalIgnoreCase) ||
                x.Contains("MovieMaker", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsYmmAssemblyName(string name)
    {
        return name.Contains("YukkuriMovieMaker", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("YukkuriMovieMaker.Plugin", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("YukkuriMovieMaker.Controls", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("YukkuriMovieMaker.Project", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeGetProcessName()
    {
        try
        {
            return Process.GetCurrentProcess().ProcessName;
        }
        catch
        {
            return "UnknownProcess";
        }
    }
}
