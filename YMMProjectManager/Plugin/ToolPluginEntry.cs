using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Presentation.ViewModels;
using YMMProjectManager.Presentation.Views;
using YukkuriMovieMaker.Plugin;

namespace YMMProjectManager.Plugin;

public sealed class ToolPluginEntry : IToolPlugin
{
    private static readonly FileLogger Logger = CreateLogger();
    private static bool exceptionHooksInstalled;

    public string Name => "プロジェクトマネージャー";
    public Type ViewModelType => typeof(ProjectListViewModel);
    public Type ViewType => typeof(ProjectListView);

    public ToolPluginEntry()
    {
        InstallGlobalExceptionHooks();
        Logger.Info("ToolPluginEntry started.");
        var timelineToolType = typeof(ProjectListViewModel);
        var hasInterface = typeof(ITimelineToolViewModel).IsAssignableFrom(timelineToolType);
        Logger.Info(
            $"Timeline context tool type check. type={timelineToolType.FullName}, assembly={timelineToolType.Assembly.GetName().Name}, implementsITimelineToolViewModel={hasInterface}");
    }

    private static FileLogger CreateLogger()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(ToolPluginEntry).Assembly.Location) ?? AppContext.BaseDirectory;
        var logPath = Path.Combine(assemblyDir, "logs", "YMMProjectManager.log");
        return new FileLogger(logPath);
    }

    private static void InstallGlobalExceptionHooks()
    {
        if (exceptionHooksInstalled)
        {
            return;
        }

        exceptionHooksInstalled = true;
        Logger.Info("Installing global exception hooks.");
        Logger.Flush();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var exText = e.ExceptionObject?.ToString() ?? "<null>";
                Logger.Info($"AppDomain.CurrentDomain.UnhandledException: {exText}");
                Logger.Flush();
            }
            catch
            {
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                Logger.Info($"TaskScheduler.UnobservedTaskException: {e.Exception}");
                Logger.Flush();
            }
            catch
            {
            }
        };

        var app = System.Windows.Application.Current;
        if (app is not null)
        {
            app.DispatcherUnhandledException += (_, e) =>
            {
                try
                {
                    Logger.Info($"Application.Current.DispatcherUnhandledException: {e.Exception}");
                    Logger.Flush();
                }
                catch
                {
                }
            };
        }
    }
}
