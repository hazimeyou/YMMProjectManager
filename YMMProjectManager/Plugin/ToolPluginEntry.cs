using System.Diagnostics;

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
        _ = Task.Run(() => TryWriteRouteARenderPerfStartupBaseline());
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

    private static void TryWriteRouteARenderPerfStartupBaseline()
    {
        try
        {
            var now = DateTimeOffset.Now;
            var current = Process.GetCurrentProcess();
            var payload = new
            {
                Timestamp = now,
                MeasurementSource = "ToolPluginEntry.ctor",
                ProcessStartTime = SafeGetStartTime(current),
                ProcessUptimeMs = SafeGetProcessUptimeMs(current),
                TotalOpenMs = 0L,
                SnapshotResolveMs = 0L,
                PipelineBuildMs = 0L,
                ViewModelCreateMs = 0L,
                MaterializationMs = 0L,
                VisibleItemsUpdateMs = 0L,
                TotalItemCount = 0,
                ProjectedItemCount = 0,
                VisibleItemCount = 0,
                InitialRenderItemCap = 0,
                InitialRenderCapApplied = false,
                ProjectionReused = false,
                ProjectionRebuilt = false,
                LastInvalidationReason = "None",
                ProjectionStatusText = "plugin-startup-baseline",
                ProcessMetrics = CaptureRelatedProcessMetrics(),
                GpuEnvironmentMetrics = new
                {
                    WpfRenderTierRaw = System.Windows.Media.RenderCapability.Tier,
                    WpfRenderTierLevel = System.Windows.Media.RenderCapability.Tier >> 16,
                    WpfHardwareRenderingAvailable = (System.Windows.Media.RenderCapability.Tier >> 16) > 0,
                    PrimaryGpuName = string.Empty,
                    DriverVersion = string.Empty,
                    DedicatedVramBytes = 0L,
                    SharedVramBytes = 0L,
                }
            };

            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "YukkuriMovieMaker_v4_Lite",
                "diagnostics");
            Directory.CreateDirectory(baseDir);
            var fileName = $"routea-render-perf-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            var path = Path.Combine(baseDir, fileName);
            var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            Logger.Info($"Startup perf baseline written: {path}");
        }
        catch (Exception ex)
        {
            Logger.Info($"Startup perf baseline write failed: {ex.Message}");
        }
    }

    private static IReadOnlyList<object> CaptureRelatedProcessMetrics()
    {
        var first = Process.GetProcesses()
            .Where(IsRelatedYmmProcess)
            .ToDictionary(p => p.Id, p => new { Cpu = SafeGetCpu(p), Mem = SafeGetWorkingSet(p), Name = p.ProcessName, Path = SafeGetMainModulePath(p), StartTime = SafeGetStartTime(p) });
        Thread.Sleep(180);
        var second = Process.GetProcesses()
            .Where(IsRelatedYmmProcess)
            .ToDictionary(p => p.Id, p => new { Cpu = SafeGetCpu(p), Mem = SafeGetWorkingSet(p), Name = p.ProcessName, Path = SafeGetMainModulePath(p), StartTime = SafeGetStartTime(p) });
        var cpuScale = 100.0 / (Environment.ProcessorCount * 0.18);
        var gpuByPid = CaptureGpuUsageByProcess();
        var now = DateTimeOffset.Now;
        return second.Select(kv =>
        {
            first.TryGetValue(kv.Key, out var prev);
            var cpuDeltaMs = (kv.Value.Cpu - (prev?.Cpu ?? kv.Value.Cpu)).TotalMilliseconds;
            return (object)new
            {
                Timestamp = now,
                ProcessId = kv.Key,
                ProcessName = kv.Value.Name,
                MainModulePath = kv.Value.Path,
                StartTime = kv.Value.StartTime,
                WorkingSetBytes = kv.Value.Mem,
                CpuPercentApprox = Math.Round(Math.Max(0, cpuDeltaMs * cpuScale), 2),
                GpuPercentApprox = Math.Round(gpuByPid.GetValueOrDefault(kv.Key), 2),
            };
        }).ToList();
    }

    private static Dictionary<int, double> CaptureGpuUsageByProcess()
    {
        var result = new Dictionary<int, double>();
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            var counters = new List<(int Pid, PerformanceCounter Counter)>();
            foreach (var instance in category.GetInstanceNames())
            {
                var pidIndex = instance.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
                if (pidIndex < 0) continue;
                var start = pidIndex + 4;
                var end = instance.IndexOf('_', start);
                var pidText = end > start ? instance[start..end] : instance[start..];
                if (!int.TryParse(pidText, out var pid)) continue;
                var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, readOnly: true);
                _ = c.NextValue();
                counters.Add((pid, c));
            }
            Thread.Sleep(120);
            foreach (var item in counters)
            {
                var value = item.Counter.NextValue();
                if (!result.TryAdd(item.Pid, value)) result[item.Pid] += value;
            }
        }
        catch
        {
        }

        return result;
    }

    private static bool IsRelatedYmmProcess(Process p)
    {
        var name = p.ProcessName ?? string.Empty;
        if (name.Contains("YukkuriMovieMaker", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("YMMProjectManager", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("YMM", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var path = SafeGetMainModulePath(p);
        return !string.IsNullOrWhiteSpace(path) && path.Contains("YukkuriMovieMaker", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan SafeGetCpu(Process p) { try { return p.TotalProcessorTime; } catch { return TimeSpan.Zero; } }
    private static long SafeGetWorkingSet(Process p) { try { return p.WorkingSet64; } catch { return 0; } }
    private static DateTimeOffset? SafeGetStartTime(Process p) { try { return p.StartTime; } catch { return null; } }
    private static string SafeGetMainModulePath(Process p) { try { return p.MainModule?.FileName ?? string.Empty; } catch { return string.Empty; } }
    private static long SafeGetProcessUptimeMs(Process p)
    {
        try { return Math.Max(0, (long)(DateTime.Now - p.StartTime).TotalMilliseconds); }
        catch { return 0; }
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
