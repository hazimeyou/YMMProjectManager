using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using YMMProjectManager.Infrastructure;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;

namespace YMMProjectManager;

//CSJ,class=TimeLineSeek,時刻行フレーム移動
public sealed class TimeLineSeek
{
    //CSJ,field=WindowSeekCommandNames,ウィンドウフレーム移動コマンド名前一覧
    private static readonly string[] WindowSeekCommandNames = ["ScrollToFrame", "シーク"];
    //CSJ,field=seekCacheLock,フレーム移動キャッシュロック
    private static readonly object seekCacheLock = new();
    //CSJ,field=cachedSeekCommand,キャッシュ済みフレーム移動コマンド
    private static object? cachedSeekCommand;
    //CSJ,field=cachedSeekInvokeMethod,キャッシュ済みフレーム移動呼び出しメソッド
    private static MethodInfo? cachedSeekInvokeMethod;
    //CSJ,field=cachedWindowSeekCommand,キャッシュ済みウィンドウフレーム移動コマンド
    private static ICommand? cachedWindowSeekCommand;

    //CSJ,field=timelineToolInfo,タイムラインツール情報
    private readonly TimelineToolInfo timelineToolInfo;

    public TimeLineSeek(TimelineToolInfo timelineToolInfo)
    {
        this.timelineToolInfo = timelineToolInfo ?? throw new ArgumentNullException(nameof(timelineToolInfo));
    }

    //CSJ,method=CreateCurrent,現在のを作成する
    public static TimeLineSeek? CreateCurrent()
    {
        //CSJ,variable=info,情報
        var info = TimelineContextService.Info;
        return info?.Timeline is null ? null : new TimeLineSeek(info);
    }

    //CSJ,method=SeekByFrames,指定フレーム数だけ移動する
    public void SeekByFrames(int frameDelta)
    {
        if (frameDelta == 0)
            return;

        //CSJ,method=SeekCore,中核を指定フレームへ移動する
        void SeekCore()
        {
            if (TryInvokeSeekCommand(frameDelta))
                return;

            //CSJ,variable=timeline,タイムライン
            var timeline = timelineToolInfo.Timeline;
            if (timeline is null)
                return;

            //CSJ,variable=currentFrame,現在のフレーム
            var currentFrame = Math.Max(0, timeline.CurrentFrame);
            timeline.CurrentFrame = Math.Max(0, currentFrame + frameDelta);
        }

        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
            dispatcher.Invoke(SeekCore);
        else
            SeekCore();
    }

    //CSJ,method=SeekToFrame,指定フレームへ移動する
    public void SeekToFrame(int targetFrame)
    {
        targetFrame = Math.Max(0, targetFrame);

        void DoSeek()
        {
            //CSJ,variable=timeline,タイムライン
            var timeline = timelineToolInfo.Timeline;
            if (timeline is null)
                return;

            //CSJ,variable=currentFrame,現在のフレーム
            var currentFrame = Math.Max(0, timeline.CurrentFrame);
            SeekByFrames(targetFrame - currentFrame);
        }

        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
            dispatcher.Invoke(DoSeek);
        else
            DoSeek();
    }

    //CSJ,method=TryInvokeSeekCommand,フレーム移動コマンドの呼び出しを試みる
    private bool TryInvokeSeekCommand(int frameDelta)
    {
        try
        {
            if (TryInvokeSeekByWindowCommand(frameDelta))
                return true;

            //CSJ,variable=seek,フレーム移動
            var seek = ResolveSeekCommandAndMethod();
            if (seek is null)
                return false;

            var args = CreateSeekArguments(seek.Value.InvokeMethod, frameDelta);
            if (args is null)
                return false;

            seek.Value.InvokeMethod.Invoke(seek.Value.Command, args);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryInvokeSeekByWindowCommand(int frameDelta)
    {
        var cmd = ResolveWindowSeekCommand();
        if (cmd is null)
            return false;

        //CSJ,variable=currentFrame,現在のフレーム
        var currentFrame = Math.Max(0, timelineToolInfo.Timeline?.CurrentFrame ?? 0);
        //CSJ,variable=targetFrame,対象フレーム
        var targetFrame = Math.Max(0, currentFrame + frameDelta);
        var args = new object?[]
        {
            targetFrame,
            frameDelta,
            FrameDeltaToTimeSpan(frameDelta),
            null
        };

        foreach (var arg in args)
        {
            try
            {
                if (!cmd.CanExecute(arg))
                    continue;

                cmd.Execute(arg);
                return true;
            }
            catch
            {
                // try next argument
            }
        }

        lock (seekCacheLock)
        {
            cachedWindowSeekCommand = null;
        }

        return false;
    }

    //CSJ,method=ResolveWindowSeekCommand,ウィンドウフレーム移動コマンドを解決する
    private static ICommand? ResolveWindowSeekCommand()
    {
        lock (seekCacheLock)
        {
            if (cachedWindowSeekCommand is not null)
                return cachedWindowSeekCommand;
        }

        //CSJ,variable=window,ウィンドウ
        var window = System.Windows.Application.Current?.MainWindow;
        if (window is null)
            return null;

        ICommand? found = null;
        //CSJ,variable=binding,バインディング
        foreach (CommandBinding binding in window.CommandBindings)
        {
            var cmd = binding.Command;
            //CSJ,variable=name,名前
            var name = GetCommandName(cmd);
            if (WindowSeekCommandNames.Any(n => string.Equals(name, n, StringComparison.Ordinal)) ||
                name.Contains("ScrollToFrame", StringComparison.OrdinalIgnoreCase))
            {
                found = cmd;
                break;
            }
        }

        if (found is null)
            return null;

        lock (seekCacheLock)
        {
            cachedWindowSeekCommand = found;
        }

        return found;
    }

    //CSJ,method=GetCommandName,コマンド名前を取得する
    private static string GetCommandName(ICommand command)
    {
        if (command is RoutedUICommand rui && !string.IsNullOrWhiteSpace(rui.Name))
            return rui.Name;

        if (command is RoutedCommand rc && !string.IsNullOrWhiteSpace(rc.Name))
            return rc.Name;

        return command.GetType().FullName ?? command.GetType().Name;
    }

    private static (object Command, MethodInfo InvokeMethod)? ResolveSeekCommandAndMethod()
    {
        lock (seekCacheLock)
        {
            if (cachedSeekCommand is not null && cachedSeekInvokeMethod is not null)
                return (cachedSeekCommand, cachedSeekInvokeMethod);
        }

        //CSJ,variable=commandSettingsType,コマンド設定型
        foreach (var commandSettingsType in ResolveCommandSettingsTypes())
        {
            if (!TryResolveSeekFromCommandSettings(commandSettingsType, out var seekCommand, out var invokeMethod))
                continue;

            lock (seekCacheLock)
            {
                cachedSeekCommand = seekCommand;
                cachedSeekInvokeMethod = invokeMethod;
            }

            return (cachedSeekCommand!, cachedSeekInvokeMethod!);
        }

        return null;
    }

    //CSJ,method=ResolveCommandSettingsTypes,コマンド設定型一覧を解決する
    private static IEnumerable<Type> ResolveCommandSettingsTypes()
    {
        var directTypeNames = new[]
        {
            "YukkuriMovieMaker.Settings.CommandSettings, YukkuriMovieMaker.Settings",
            "YukkuriMovieMaker.Settings.CommandSettings, YukkuriMovieMaker.Plugin",
            "YukkuriMovieMaker.Settings.CommandSettings, YukkuriMovieMaker",
        };

        //CSJ,variable=typeName,型名前
        foreach (var typeName in directTypeNames)
        {
            //CSJ,variable=type,型
            var type = Type.GetType(typeName, throwOnError: false);
            if (type is not null)
                yield return type;
        }
    }

    private static bool TryResolveSeekFromCommandSettings(Type commandSettingsType, out object? seekCommand, out MethodInfo? invokeMethod)
    {
        seekCommand = null;
        invokeMethod = null;

        //CSJ,variable=defaultProperty,既定プロパティ
        var defaultProperty = commandSettingsType.GetProperty("Default", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        //CSJ,variable=settingsInstance,設定インスタンス
        var settingsInstance = defaultProperty?.GetValue(null);
        if (settingsInstance is null)
            return false;

        //CSJ,variable=instanceType,インスタンス型
        var instanceType = settingsInstance.GetType();
        //CSJ,variable=indexer,索引作成
        var indexer = instanceType.GetProperty("Item", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, null, new[] { typeof(string) }, null);
        if (indexer is null)
            return false;

        seekCommand = indexer.GetValue(settingsInstance, new object[] { "Seek" })
            ?? indexer.GetValue(settingsInstance, new object[] { "[Seek]" });
        if (seekCommand is null)
            return false;

        invokeMethod = seekCommand.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
            {
                if (!string.Equals(m.Name, "Invoke", StringComparison.Ordinal) &&
                    !string.Equals(m.Name, "Execute", StringComparison.Ordinal))
                    return false;

                var p = m.GetParameters();
                if (p.Length == 1)
                    return p[0].ParameterType == typeof(int) || p[0].ParameterType == typeof(TimeSpan) || p[0].ParameterType == typeof(object);

                if (p.Length == 2)
                    return p[0].ParameterType == typeof(object) && typeof(IInputElement).IsAssignableFrom(p[1].ParameterType);

                return false;
            });

        return invokeMethod is not null;
    }

    //CSJ,method=CreateSeekArguments,フレーム移動引数一覧を作成する
    private object?[]? CreateSeekArguments(MethodInfo invokeMethod, int frameDelta)
    {
        var parameters = invokeMethod.GetParameters();
        if (parameters.Length == 1)
        {
            //CSJ,variable=parameterType,引数型
            var parameterType = parameters[0].ParameterType;
            object value = parameterType == typeof(TimeSpan)
                ? FrameDeltaToTimeSpan(frameDelta)
                : frameDelta;
            return new object?[] { value };
        }

        if (parameters.Length == 2)
            return new object?[] { frameDelta, System.Windows.Application.Current?.MainWindow };

        return null;
    }

    private TimeSpan FrameDeltaToTimeSpan(int frameDelta)
    {
        //CSJ,variable=timeline,タイムライン
        var timeline = timelineToolInfo.Timeline;
        if (timeline is null)
            return TimeSpan.Zero;

        var fps = GetTimelineFps(timeline);
        if (fps <= 0)
            fps = 60.0;

        return TimeSpan.FromSeconds(frameDelta / fps);
    }

    //CSJ,method=GetTimelineFps,タイムラインFPSを取得する
    private static double GetTimelineFps(Timeline timeline)
    {
        try
        {
            //CSJ,variable=videoInfoProperty,動画情報プロパティ
            var videoInfoProperty = timeline.GetType().GetProperty("VideoInfo", BindingFlags.Public | BindingFlags.Instance);
            //CSJ,variable=videoInfo,動画情報
            var videoInfo = videoInfoProperty?.GetValue(timeline);
            if (videoInfo is not null)
            {
                //CSJ,variable=fpsValue,FPS値
                var fpsValue = GetPropertyDouble(videoInfo, "FPS");
                if (fpsValue > 0)
                    return fpsValue;
            }

            var directFps = GetPropertyDouble(timeline, "FPS");
            if (directFps > 0)
                return directFps;

            var frameRate = GetPropertyDouble(timeline, "FrameRate");
            if (frameRate > 0)
                return frameRate;
        }
        catch
        {
            // fallback below
        }

        return 60.0;
    }

    private static double GetPropertyDouble(object instance, string propertyName)
    {
        //CSJ,variable=property,プロパティ
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property is null)
            return 0;

        var value = property.GetValue(instance);
        return value switch
        {
            double d => d,
            float f => f,
            decimal m => (double)m,
            int i => i,
            long l => l,
            string s when double.TryParse(s, out var parsed) => parsed,
            _ => 0,
        };
    }
}
