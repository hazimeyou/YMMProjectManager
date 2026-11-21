using System;
using System.Reflection;

namespace YMMProjectManager.Infrastructure.Output;

public static class YmmProjectPathResolver
{
    public static string? TryGetCurrentProjectPath()
    {
        try
        {
            var type = Type.GetType("YukkuriMovieMaker.Settings.ResourceDirectorySettings, YukkuriMovieMaker.Plugin", false);
            var method = type?.GetMethod("GetProjectFilePath", BindingFlags.NonPublic | BindingFlags.Static);
            return method?.Invoke(null, null) as string;
        }
        catch
        {
            return null;
        }
    }
}

