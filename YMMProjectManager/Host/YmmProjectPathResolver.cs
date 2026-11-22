using System;
using System.Reflection;

namespace YMMProjectManager.Infrastructure.Output;

//CSJ,class=YmmProjectPathResolver,YMMプロジェクトのパス解決処理
public static class YmmProjectPathResolver
{
    //CSJ,method=TryGetCurrentProjectPath,現在のプロジェクトのパスの取得を試みる
    public static string? TryGetCurrentProjectPath()
    {
        try
        {
            //CSJ,variable=type,型
            var type = Type.GetType("YukkuriMovieMaker.Settings.ResourceDirectorySettings, YukkuriMovieMaker.Plugin", false);
            //CSJ,variable=method,メソッド
            var method = type?.GetMethod("GetProjectFilePath", BindingFlags.NonPublic | BindingFlags.Static);
            return method?.Invoke(null, null) as string;
        }
        catch
        {
            return null;
        }
    }
}

