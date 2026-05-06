using System.Windows;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed class ExperimentalYmmTimelineHostViewModel : ViewModelBase, IDisposable
{
    private readonly YmmTimelineReflectionProbe probe = new();
    private readonly Stopwatch initializeStopwatch = new();
    private readonly Stopwatch disposeStopwatch = new();
    private readonly ObservableCollection<YmmTimelineReflectionLog> logs = [];
    private readonly ObservableCollection<string> constructorSignatures = [];
    private readonly ObservableCollection<string> missingDependencies = [];
    private readonly ObservableCollection<string> foundAssemblies = [];
    private string status = "Not initialized";
    private string summary = string.Empty;
    private bool disposed;

    public ReadOnlyObservableCollection<YmmTimelineReflectionLog> Logs { get; }
    public ReadOnlyObservableCollection<string> ConstructorSignatures { get; }
    public ReadOnlyObservableCollection<string> MissingDependencies { get; }
    public ReadOnlyObservableCollection<string> FoundAssemblies { get; }

    public string Status
    {
        get => status;
        private set => SetProperty(ref status, value);
    }

    public string Summary
    {
        get => summary;
        private set => SetProperty(ref summary, value);
    }

    public FrameworkElement? TimelineViewElement { get; private set; }

    public long InitializeMs { get; private set; }
    public long DisposeMs { get; private set; }

    public YmmTimelineReflectionResult? ReflectionResult { get; private set; }

    public string ReflectionSummary =>
        ReflectionResult is null
            ? "(no reflection result)"
            : $"ready={ReflectionResult.CanAttemptExperimentalHost}, found={ReflectionResult.TypeFoundCount}, missing={ReflectionResult.MissingDependencies.Count}";

    public string TimelineViewType => ReflectionResult?.TimelineViewTypeName ?? "(not found)";
    public string TimelineViewModelType => ReflectionResult?.TimelineViewModelTypeName ?? "(not found)";
    public string SetTimelineToolInfoOwnerType => ReflectionResult?.SetTimelineToolInfoOwnerTypeName ?? "(not found)";

    public bool TryInitialize(bool useReflection)
    {
        if (disposed)
        {
            logs.Add(new YmmTimelineReflectionLog { Category = "Init", Message = "Initialization skipped: already disposed." });
            return false;
        }

        initializeStopwatch.Restart();
        logs.Clear();
        logs.Add(new YmmTimelineReflectionLog { Category = "Init", Message = $"Initialize start (useReflection={useReflection})" });

        try
        {
            if (!useReflection)
            {
                Status = "ReflectionDisabled";
                Summary = "UseReflection=false";
                return false;
            }

            var result = probe.Probe(logs);
            ReflectionResult = result;
            ReplaceCollection(constructorSignatures, result.ConstructorSignatures);
            ReplaceCollection(missingDependencies, result.MissingDependencies);
            ReplaceCollection(foundAssemblies, result.FoundAssemblies);
            OnPropertyChanged(nameof(ReflectionResult));
            OnPropertyChanged(nameof(ReflectionSummary));
            OnPropertyChanged(nameof(TimelineViewType));
            OnPropertyChanged(nameof(TimelineViewModelType));
            OnPropertyChanged(nameof(SetTimelineToolInfoOwnerType));

            SaveDiagnostics(result, logs);
            PureTimelineDiagnostics.UpdateTimelineReflectionMetrics(result.ProbeMs, result.AssemblyCount, result.TypeFoundCount);

            if (result.CanAttemptExperimentalHost)
            {
                Status = "ExperimentalReady";
                Summary = "Reflection probe succeeded enough for experimental host attempt.";
                return true;
            }

            Status = "Unavailable";
            Summary = "Reflection probe completed, but required types/dependencies are missing.";
            return false;
        }
        catch (Exception ex)
        {
            PureTimelineDiagnostics.IncrementTimelineReflectionFailureCount();
            logs.Add(new YmmTimelineReflectionLog { Category = "Error", Message = $"Probe exception: {ex.GetType().Name}: {ex.Message}" });
            Status = "InitializeFailed";
            Summary = ex.Message;
            return false;
        }
        finally
        {
            initializeStopwatch.Stop();
            InitializeMs = initializeStopwatch.ElapsedMilliseconds;
            OnPropertyChanged(nameof(InitializeMs));
            logs.Add(new YmmTimelineReflectionLog { Category = "Init", Message = $"Initialize finished in {InitializeMs} ms" });
        }
    }

    private static void SaveDiagnostics(YmmTimelineReflectionResult result, IEnumerable<YmmTimelineReflectionLog> logs)
    {
        try
        {
            var dir = Path.Combine(Environment.CurrentDirectory, "logs", "diagnostics");
            Directory.CreateDirectory(dir);
            var output = new
            {
                timestamp = DateTimeOffset.Now,
                result,
                logs = logs.Select(x => new { x.Timestamp, x.Category, x.Message }).ToList(),
            };
            var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
            var path = Path.Combine(dir, $"timeline-reflection-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, json, Encoding.UTF8);
        }
        catch
        {
            // Diagnostics output failure must not break flow.
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposeStopwatch.Restart();
        try
        {
            TimelineViewElement = null;
            ReflectionResult = null;
            constructorSignatures.Clear();
            missingDependencies.Clear();
            foundAssemblies.Clear();
            Status = "Disposed";
            Summary = "Disposed experimental host view model.";
            OnPropertyChanged(nameof(ReflectionResult));
            OnPropertyChanged(nameof(ReflectionSummary));
            OnPropertyChanged(nameof(TimelineViewType));
            OnPropertyChanged(nameof(TimelineViewModelType));
            OnPropertyChanged(nameof(SetTimelineToolInfoOwnerType));
        }
        finally
        {
            disposed = true;
            disposeStopwatch.Stop();
            DisposeMs = disposeStopwatch.ElapsedMilliseconds;
            OnPropertyChanged(nameof(DisposeMs));
            logs.Add(new YmmTimelineReflectionLog { Category = "Dispose", Message = $"Dispose finished in {DisposeMs} ms" });
        }
    }

    public ExperimentalYmmTimelineHostViewModel()
    {
        Logs = new ReadOnlyObservableCollection<YmmTimelineReflectionLog>(logs);
        ConstructorSignatures = new ReadOnlyObservableCollection<string>(constructorSignatures);
        MissingDependencies = new ReadOnlyObservableCollection<string>(missingDependencies);
        FoundAssemblies = new ReadOnlyObservableCollection<string>(foundAssemblies);
    }

    private static void ReplaceCollection(ObservableCollection<string> target, IEnumerable<string> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}
