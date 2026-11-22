using System.IO;
using YukkuriMovieMaker.Plugin;

namespace YMMProjectManager.Infrastructure;

internal static class PluginDiagnostics
{
    internal static FileLogger Logger { get; } = new(Path.Combine(
        Path.GetDirectoryName(typeof(PluginDiagnostics).Assembly.Location) ?? AppContext.BaseDirectory,
        "logs", "YMMProjectManager.log"));

    private static readonly object Sync = new();
    private static bool processHooksInstalled;
    private static System.Windows.Application? dispatcherSource;

    internal static void Start(Type viewModelType)
    {
        lock (Sync)
        {
            if (!processHooksInstalled)
            {
                Logger.Info("Installing global exception hooks.");
                AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                    LogException("AppDomain.CurrentDomain.UnhandledException", e.ExceptionObject);
                TaskScheduler.UnobservedTaskException += (_, e) =>
                    LogException("TaskScheduler.UnobservedTaskException", e.Exception);
                processHooksInstalled = true;
            }

            var app = System.Windows.Application.Current;
            if (app is not null && dispatcherSource != app)
            {
                if (dispatcherSource is not null)
                    dispatcherSource.DispatcherUnhandledException -= OnDispatcherException;
                app.DispatcherUnhandledException += OnDispatcherException;
                dispatcherSource = app;
            }
        }

        Logger.Info("ToolPluginEntry started.");
        Logger.Info($"Timeline context tool type check. type={viewModelType.FullName}, assembly={viewModelType.Assembly.GetName().Name}, implementsITimelineToolViewModel={typeof(ITimelineToolViewModel).IsAssignableFrom(viewModelType)}");
    }

    private static void OnDispatcherException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        => LogException("Application.Current.DispatcherUnhandledException", e.Exception);

    private static void LogException(string source, object? error)
    {
        try
        {
            Logger.Info($"{source}: {error?.ToString() ?? "<null>"}");
            Logger.Flush();
        }
        catch
        {
            // Diagnostics must not interfere with the host's exception handling.
        }
    }
}
