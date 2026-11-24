using System.Diagnostics;
using System.IO;
using System.Runtime.Loader;
using System.Windows.Threading;
using YukkuriMovieMaker.Plugin;

namespace YMMProjectManager.Infrastructure;

internal static class PluginDiagnostics
{
    internal static FileLogger Logger { get; } = new(Path.Combine(
        Path.GetDirectoryName(typeof(PluginDiagnostics).Assembly.Location) ?? AppContext.BaseDirectory,
        "logs", "YMMProjectManager.log"));

    private static readonly object Sync = new();
    private static bool processHooksInstalled;
    private static bool stopping;
    private static System.Windows.Application? dispatcherSource;

    internal static void Start(Type viewModelType)
    {
        lock (Sync)
        {
            if (stopping) return;
            if (!processHooksInstalled)
            {
                Logger.Info("Installing global exception hooks.");
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
                var loadContext = AssemblyLoadContext.GetLoadContext(typeof(PluginDiagnostics).Assembly);
                if (loadContext is not null) loadContext.Unloading += Stop;
                processHooksInstalled = true;
            }

            var app = System.Windows.Application.Current;
            if (app is not null && dispatcherSource != app)
            {
                DetachDispatcher();
                app.DispatcherUnhandledException += OnDispatcherException;
                app.Dispatcher.ShutdownFinished += OnDispatcherShutdown;
                dispatcherSource = app;
            }
            Logger.Info("ToolPluginEntry started.");
            Logger.Info($"Timeline context tool type check. type={viewModelType.FullName}, assembly={viewModelType.Assembly.GetName().Name}, implementsITimelineToolViewModel={typeof(ITimelineToolViewModel).IsAssignableFrom(viewModelType)}");
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => LogException("AppDomain.CurrentDomain.UnhandledException", e.ExceptionObject);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        => LogException("TaskScheduler.UnobservedTaskException", e.Exception);

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
        => LogException("Application.Current.DispatcherUnhandledException", e.Exception);

    private static void OnDispatcherShutdown(object? sender, EventArgs e)
    {
        lock (Sync)
            if (ReferenceEquals(dispatcherSource?.Dispatcher, sender)) DetachDispatcher();
    }

    private static void DetachDispatcher()
    {
        if (dispatcherSource is null) return;
        dispatcherSource.DispatcherUnhandledException -= OnDispatcherException;
        dispatcherSource.Dispatcher.ShutdownFinished -= OnDispatcherShutdown;
        dispatcherSource = null;
    }

    private static void Stop(AssemblyLoadContext loadContext)
    {
        lock (Sync)
        {
            stopping = true;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            loadContext.Unloading -= Stop;
            DetachDispatcher();
            Logger.Close();
        }
    }

    private static void LogException(string source, object? error)
    {
        try
        {
            lock (Sync)
                if (!stopping && error is Exception exception)
                    Logger.Error(source, exception);
                else if (!stopping)
                    Logger.Info(source);
        }
        catch
        {
            // Diagnostics must not interfere with the host's exception handling.
        }
    }

    internal static string DescribeException(object? error)
    {
        if (error is not Exception exception) return error?.GetType().FullName ?? "<null>";
        // Messages, Data and source paths can include project data or credentials from the host.
        return $"{exception.GetType().FullName} (0x{exception.HResult:X8}){Environment.NewLine}{new StackTrace(exception, fNeedFileInfo: false)}";
    }
}
