using System.Windows;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed class ExperimentalYmmTimelineHostViewModel : ViewModelBase, IDisposable
{
    private readonly YmmTimelineReflectionProbe probe = new();
    private readonly YmmTimelineConstructorBinder constructorBinder = new();
    private readonly Stopwatch initializeStopwatch = new();
    private readonly Stopwatch disposeStopwatch = new();
    private readonly ObservableCollection<YmmTimelineReflectionLog> logs = [];
    private readonly ObservableCollection<string> constructorSignatures = [];
    private readonly ObservableCollection<string> constructorBindingSummary = [];
    private readonly ObservableCollection<string> blockingReasons = [];
    private readonly ObservableCollection<string> readinessWarnings = [];
    private readonly ObservableCollection<string> missingDependencies = [];
    private readonly ObservableCollection<string> foundAssemblies = [];
    private string status = "Not initialized";
    private string summary = string.Empty;
    private bool disposed;

    public ReadOnlyObservableCollection<YmmTimelineReflectionLog> Logs { get; }
    public ReadOnlyObservableCollection<string> ConstructorSignatures { get; }
    public ReadOnlyObservableCollection<string> ConstructorBindingSummary { get; }
    public ReadOnlyObservableCollection<string> BlockingReasons { get; }
    public ReadOnlyObservableCollection<string> ReadinessWarnings { get; }
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
    public IReadOnlyList<YmmTimelineConstructorBindingResult> TimelineViewBindingResults { get; private set; } = [];
    public IReadOnlyList<YmmTimelineConstructorBindingResult> TimelineViewModelBindingResults { get; private set; } = [];
    public YmmTimelineGenerationReadiness? GenerationReadiness { get; private set; }

    public string ReflectionSummary =>
        ReflectionResult is null
            ? "(no reflection result)"
            : $"ready={ReflectionResult.CanAttemptExperimentalHost}, found={ReflectionResult.TypeFoundCount}, missing={ReflectionResult.MissingDependencies.Count}";

    public string TimelineViewType => ReflectionResult?.TimelineViewTypeName ?? "(not found)";
    public string TimelineViewModelType => ReflectionResult?.TimelineViewModelTypeName ?? "(not found)";
    public string SetTimelineToolInfoOwnerType => ReflectionResult?.SetTimelineToolInfoOwnerTypeName ?? "(not found)";
    public int ReadinessScore => GenerationReadiness?.Score ?? 0;
    public bool CanAttemptViewModelGeneration => GenerationReadiness?.CanAttemptViewModelGeneration ?? false;
    public bool CanAttemptViewGeneration => GenerationReadiness?.CanAttemptViewGeneration ?? false;
    public bool ReadinessSufficient => ReadinessScore >= 70;

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

            var bindingStopwatch = Stopwatch.StartNew();
            var timelineViewType = ResolveType(result.TimelineViewTypeName);
            var timelineViewModelType = ResolveType(result.TimelineViewModelTypeName);
            TimelineViewBindingResults = constructorBinder.DryRunForType(timelineViewType, logs);
            TimelineViewModelBindingResults = constructorBinder.DryRunForType(timelineViewModelType, logs);
            GenerationReadiness = constructorBinder.BuildReadiness(result, TimelineViewBindingResults, TimelineViewModelBindingResults);
            bindingStopwatch.Stop();
            PureTimelineDiagnostics.UpdateTimelineConstructorBindingMetrics(
                bindingStopwatch.ElapsedMilliseconds,
                TimelineViewBindingResults.Count + TimelineViewModelBindingResults.Count,
                TimelineViewBindingResults.Count(x => x.CanAttemptGeneration) + TimelineViewModelBindingResults.Count(x => x.CanAttemptGeneration),
                GenerationReadiness.Score,
                GenerationReadiness.BlockingReasons.Count);
            RefreshBindingCollections();

            SaveDiagnostics(result, TimelineViewBindingResults, TimelineViewModelBindingResults, GenerationReadiness, logs);
            PureTimelineDiagnostics.UpdateTimelineReflectionMetrics(result.ProbeMs, result.AssemblyCount, result.TypeFoundCount);
            OnPropertyChanged(nameof(ReadinessScore));
            OnPropertyChanged(nameof(CanAttemptViewModelGeneration));
            OnPropertyChanged(nameof(CanAttemptViewGeneration));
            OnPropertyChanged(nameof(ReadinessSufficient));

            if (GenerationReadiness.Score >= 70 && GenerationReadiness.CanAttemptViewModelGeneration)
            {
                Status = "ExperimentalReady";
                Summary = $"Readiness: {GenerationReadiness.Score} / Constructor binding mostly resolved.";
                return true;
            }

            Status = "Unavailable";
            Summary = $"Readiness: {GenerationReadiness.Score} / {string.Join(" ; ", GenerationReadiness.BlockingReasons.DefaultIfEmpty("Required dependencies are missing."))}";
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

    private static void SaveDiagnostics(
        YmmTimelineReflectionResult result,
        IReadOnlyList<YmmTimelineConstructorBindingResult> viewBindings,
        IReadOnlyList<YmmTimelineConstructorBindingResult> viewModelBindings,
        YmmTimelineGenerationReadiness? readiness,
        IEnumerable<YmmTimelineReflectionLog> logs)
    {
        try
        {
            var dir = Path.Combine(Environment.CurrentDirectory, "logs", "diagnostics");
            Directory.CreateDirectory(dir);
            var output = new
            {
                timestamp = DateTimeOffset.Now,
                reflection = result,
                timelineViewConstructorBindings = viewBindings,
                timelineViewModelConstructorBindings = viewModelBindings,
                readiness,
                logs = logs.Select(x => new { x.Timestamp, x.Category, x.Message }).ToList(),
            };
            var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
            var path = Path.Combine(dir, $"timeline-binding-{DateTime.Now:yyyyMMdd-HHmmss}.json");
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
            TimelineViewBindingResults = [];
            TimelineViewModelBindingResults = [];
            GenerationReadiness = null;
            constructorSignatures.Clear();
            constructorBindingSummary.Clear();
            blockingReasons.Clear();
            readinessWarnings.Clear();
            missingDependencies.Clear();
            foundAssemblies.Clear();
            Status = "Disposed";
            Summary = "Disposed experimental host view model.";
            OnPropertyChanged(nameof(ReflectionResult));
            OnPropertyChanged(nameof(ReflectionSummary));
            OnPropertyChanged(nameof(TimelineViewType));
            OnPropertyChanged(nameof(TimelineViewModelType));
            OnPropertyChanged(nameof(SetTimelineToolInfoOwnerType));
            OnPropertyChanged(nameof(ReadinessScore));
            OnPropertyChanged(nameof(CanAttemptViewModelGeneration));
            OnPropertyChanged(nameof(CanAttemptViewGeneration));
            OnPropertyChanged(nameof(ReadinessSufficient));
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
        ConstructorBindingSummary = new ReadOnlyObservableCollection<string>(constructorBindingSummary);
        BlockingReasons = new ReadOnlyObservableCollection<string>(blockingReasons);
        ReadinessWarnings = new ReadOnlyObservableCollection<string>(readinessWarnings);
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

    private void RefreshBindingCollections()
    {
        constructorBindingSummary.Clear();
        foreach (var binding in TimelineViewModelBindingResults.Concat(TimelineViewBindingResults))
        {
            constructorBindingSummary.Add($"{binding.TargetTypeName}: {binding.ConstructorSignature} => {(binding.CanAttemptGeneration ? "OK" : "NG")}");
            foreach (var parameter in binding.Parameters)
            {
                var state = parameter.CanResolve ? "OK" : $"NG ({parameter.FailureReason})";
                constructorBindingSummary.Add($"  - {parameter.ParameterName}: {state}");
            }
        }

        ReplaceCollection(blockingReasons, GenerationReadiness?.BlockingReasons ?? []);
        ReplaceCollection(readinessWarnings, GenerationReadiness?.Warnings ?? []);
    }

    private static Type? ResolveType(string? fullTypeName)
    {
        if (string.IsNullOrWhiteSpace(fullTypeName))
        {
            return null;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(fullTypeName, false, false);
            if (type is not null)
            {
                return type;
            }
        }

        return null;
    }
}
