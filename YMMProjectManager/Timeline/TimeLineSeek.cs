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

public sealed class TimeLineSeek
{
    private static readonly string[] WindowSeekCommandNames = ["ScrollToFrame", "シーク"];
    private static readonly object seekCacheLock = new();
    private static object? cachedSeekCommand;
    private static MethodInfo? cachedSeekInvokeMethod;
    private static ICommand? cachedWindowSeekCommand;

    private readonly TimelineToolInfo timelineToolInfo;

    public TimeLineSeek(TimelineToolInfo timelineToolInfo)
    {
        this.timelineToolInfo = timelineToolInfo ?? throw new ArgumentNullException(nameof(timelineToolInfo));
    }

    public static TimeLineSeek? CreateCurrent()
    {
        var info = TimelineContextService.Info;
        return info?.Timeline is null ? null : new TimeLineSeek(info);
    }

    public void SeekByFrames(int frameDelta)
    {
        if (frameDelta == 0)
            return;

        void SeekCore()
        {
            if (TryInvokeSeekCommand(frameDelta))
                return;

            var timeline = timelineToolInfo.Timeline;
            if (timeline is null)
                return;

            var currentFrame = Math.Max(0, timeline.CurrentFrame);
            timeline.CurrentFrame = Math.Max(0, currentFrame + frameDelta);
        }

        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
            dispatcher.Invoke(SeekCore);
        else
            SeekCore();
    }

    public void SeekToFrame(int targetFrame)
    {
        targetFrame = Math.Max(0, targetFrame);

        void DoSeek()
        {
            var timeline = timelineToolInfo.Timeline;
            if (timeline is null)
                return;

            var currentFrame = Math.Max(0, timeline.CurrentFrame);
            SeekByFrames(targetFrame - currentFrame);
        }

        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
            dispatcher.Invoke(DoSeek);
        else
            DoSeek();
    }

    private bool TryInvokeSeekCommand(int frameDelta)
    {
        try
        {
            if (TryInvokeSeekByWindowCommand(frameDelta))
                return true;

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

        var currentFrame = Math.Max(0, timelineToolInfo.Timeline?.CurrentFrame ?? 0);
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

    private static ICommand? ResolveWindowSeekCommand()
    {
        lock (seekCacheLock)
        {
            if (cachedWindowSeekCommand is not null)
                return cachedWindowSeekCommand;
        }

        var window = System.Windows.Application.Current?.MainWindow;
        if (window is null)
            return null;

        ICommand? found = null;
        foreach (CommandBinding binding in window.CommandBindings)
        {
            var cmd = binding.Command;
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

    private static IEnumerable<Type> ResolveCommandSettingsTypes()
    {
        var directTypeNames = new[]
        {
            "YukkuriMovieMaker.Settings.CommandSettings, YukkuriMovieMaker.Settings",
            "YukkuriMovieMaker.Settings.CommandSettings, YukkuriMovieMaker.Plugin",
            "YukkuriMovieMaker.Settings.CommandSettings, YukkuriMovieMaker",
        };

        foreach (var typeName in directTypeNames)
        {
            var type = Type.GetType(typeName, throwOnError: false);
            if (type is not null)
                yield return type;
        }
    }

    private static bool TryResolveSeekFromCommandSettings(Type commandSettingsType, out object? seekCommand, out MethodInfo? invokeMethod)
    {
        seekCommand = null;
        invokeMethod = null;

        var defaultProperty = commandSettingsType.GetProperty("Default", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var settingsInstance = defaultProperty?.GetValue(null);
        if (settingsInstance is null)
            return false;

        var instanceType = settingsInstance.GetType();
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

    private object?[]? CreateSeekArguments(MethodInfo invokeMethod, int frameDelta)
    {
        var parameters = invokeMethod.GetParameters();
        if (parameters.Length == 1)
        {
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
        var timeline = timelineToolInfo.Timeline;
        if (timeline is null)
            return TimeSpan.Zero;

        var fps = GetTimelineFps(timeline);
        if (fps <= 0)
            fps = 60.0;

        return TimeSpan.FromSeconds(frameDelta / fps);
    }

    private static double GetTimelineFps(Timeline timeline)
    {
        try
        {
            var videoInfoProperty = timeline.GetType().GetProperty("VideoInfo", BindingFlags.Public | BindingFlags.Instance);
            var videoInfo = videoInfoProperty?.GetValue(timeline);
            if (videoInfo is not null)
            {
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
