using System.Windows;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed class ExperimentalYmmTimelineHostViewModel : ViewModelBase, IDisposable
{
    private readonly YmmTimelineReflectionProbe probe = new();
    private readonly YmmTimelineConstructorBinder constructorBinder = new();
    private readonly YmmTimelineViewModelGenerationAttempt generationAttempt = new();
    private readonly RuntimeEnvironmentDetector runtimeEnvironmentDetector = new();
    private readonly Stopwatch initializeStopwatch = new();
    private readonly Stopwatch disposeStopwatch = new();
    private readonly ObservableCollection<YmmTimelineReflectionLog> logs = [];
    private readonly ObservableCollection<string> constructorSignatures = [];
    private readonly ObservableCollection<string> constructorBindingSummary = [];
    private readonly ObservableCollection<string> blockingReasons = [];
    private readonly ObservableCollection<string> readinessWarnings = [];
    private readonly ObservableCollection<string> missingDependencies = [];
    private readonly ObservableCollection<string> foundAssemblies = [];
    private readonly ObservableCollection<string> ymmRelatedAssemblies = [];
    private readonly ObservableCollection<string> candidateAssemblies = [];
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
    public ReadOnlyObservableCollection<string> YmmRelatedAssemblies { get; }
    public ReadOnlyObservableCollection<string> CandidateAssemblies { get; }

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
    public YmmTimelineGenerationAttemptResult? GenerationAttemptResult { get; private set; }

    public string ReflectionSummary =>
        ReflectionResult is null
            ? "(no reflection result)"
            : $"ready={ReflectionResult.CanAttemptExperimentalHost}, found={ReflectionResult.TypeFoundCount}, missing={ReflectionResult.MissingDependencies.Count}";

    public string TimelineViewType => ReflectionResult?.TimelineViewTypeName ?? "(not found)";
    public string TimelineViewModelType => ReflectionResult?.TimelineViewModelTypeName ?? "(not found)";
    public string SetTimelineToolInfoOwnerType => ReflectionResult?.SetTimelineToolInfoOwnerTypeName ?? "(not found)";
    public string RuntimeKindText => ReflectionResult?.RuntimeKind.ToString() ?? runtimeEnvironmentDetector.Detect().ToString();
    public string RuntimeProcessName => ReflectionResult?.ProcessName ?? runtimeEnvironmentDetector.GetProcessName();
    public int ReadinessScore => GenerationReadiness?.Score ?? 0;
    public bool CanAttemptViewModelGeneration => GenerationReadiness?.CanAttemptViewModelGeneration ?? false;
    public bool CanAttemptViewGeneration => GenerationReadiness?.CanAttemptViewGeneration ?? false;
    public bool ReadinessSufficient => ReadinessScore >= 70;
    public bool GenerationAttempted => GenerationAttemptResult?.Attempted ?? false;
    public bool GenerationSucceeded => GenerationAttemptResult?.Succeeded ?? false;
    public string GenerationFailureReason => GenerationAttemptResult?.FailureReason ?? string.Empty;
    public string GenerationConstructorSignature => GenerationAttemptResult?.ConstructorSignature ?? string.Empty;
    public string GenerationException => GenerationAttemptResult?.ExceptionMessage ?? string.Empty;
    public bool DisposeAttempted => GenerationAttemptResult?.DisposeAttempted ?? false;
    public bool DisposeSucceeded => GenerationAttemptResult?.DisposeSucceeded ?? false;
    public string DisposeFailureReason => GenerationAttemptResult?.DisposeFailureReason ?? string.Empty;

    public bool TryInitialize(PureTimelineExperimentalOptions options)
    {
        if (disposed)
        {
            logs.Add(new YmmTimelineReflectionLog { Category = "Init", Message = "Initialization skipped: already disposed." });
            return false;
        }

        initializeStopwatch.Restart();
        logs.Clear();
        logs.Add(new YmmTimelineReflectionLog { Category = "Init", Message = $"Initialize start (useReflection={options.UseReflection})" });

        try
        {
            if (!options.UseReflection)
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
            ReplaceCollection(ymmRelatedAssemblies, result.YmmRelatedAssemblyNames);
            ReplaceCollection(candidateAssemblies, result.CandidateAssemblyNames);
            OnPropertyChanged(nameof(ReflectionResult));
            OnPropertyChanged(nameof(ReflectionSummary));
            OnPropertyChanged(nameof(TimelineViewType));
            OnPropertyChanged(nameof(TimelineViewModelType));
            OnPropertyChanged(nameof(SetTimelineToolInfoOwnerType));
            OnPropertyChanged(nameof(RuntimeKindText));
            OnPropertyChanged(nameof(RuntimeProcessName));

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

            var gatePassed =
                options.EnableExperimentalYmmTimelineHost &&
                options.AllowViewModelGenerationAttempt &&
                GenerationReadiness.Score >= options.MinimumReadinessScoreForGeneration &&
                GenerationReadiness.CanAttemptViewModelGeneration;

            if (gatePassed)
            {
                var selectedBinding = TimelineViewModelBindingResults
                    .Where(x => x.CanAttemptGeneration)
                    .OrderByDescending(x => x.Parameters.Count)
                    .FirstOrDefault();
                var vmType = ResolveType(result.TimelineViewModelTypeName);
                if (selectedBinding is not null && vmType is not null)
                {
                    GenerationAttemptResult = generationAttempt
                        .TryGenerateAndDisposeAsync(vmType, selectedBinding, options.DisposeImmediatelyAfterGeneration)
                        .GetAwaiter().GetResult();
                    PureTimelineDiagnostics.UpdateTimelineViewModelGenerationMetrics(GenerationAttemptResult);
                    OnPropertyChanged(nameof(GenerationAttempted));
                    OnPropertyChanged(nameof(GenerationSucceeded));
                    OnPropertyChanged(nameof(GenerationFailureReason));
                    OnPropertyChanged(nameof(GenerationConstructorSignature));
                    OnPropertyChanged(nameof(GenerationException));
                    OnPropertyChanged(nameof(DisposeAttempted));
                    OnPropertyChanged(nameof(DisposeSucceeded));
                    OnPropertyChanged(nameof(DisposeFailureReason));
                    SaveGenerationAttemptDiagnostics(result, TimelineViewBindingResults, TimelineViewModelBindingResults, GenerationReadiness, GenerationAttemptResult, logs);

                    if (GenerationAttemptResult.Succeeded && (!GenerationAttemptResult.DisposeAttempted || GenerationAttemptResult.DisposeSucceeded))
                    {
                        Status = "ExperimentalReady";
                        Summary = "ViewModel generation and immediate dispose succeeded.";
                        return true;
                    }

                    Status = "Unavailable";
                    Summary = $"Generation failed: {GenerationAttemptResult.FailureReason ?? GenerationAttemptResult.ExceptionMessage}";
                    return false;
                }
            }

            if (GenerationReadiness.Score >= 70 && GenerationReadiness.CanAttemptViewModelGeneration)
            {
                Status = "ExperimentalReady";
                Summary = options.AllowViewModelGenerationAttempt
                    ? $"Generation skipped: gate unresolved (score={GenerationReadiness.Score})."
                    : $"Readiness: {GenerationReadiness.Score} / Generation attempt is disabled by default.";
                return true;
            }

            Status = "Unavailable";
            Summary = $"Readiness: {GenerationReadiness.Score} / {string.Join(" ; ", GenerationReadiness.BlockingReasons.DefaultIfEmpty("Required dependencies are missing."))}";
            if (result.RuntimeKind == RuntimeEnvironmentKind.Benchmark)
            {
                Summary += " / Benchmark環境では YMM Timeline 型は通常見つかりません。";
            }
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
                runtimeKind = result.RuntimeKind.ToString(),
                processName = result.ProcessName,
                loadedAssemblyCount = result.AssemblyCount,
                ymmAssemblyNames = result.YmmRelatedAssemblyNames,
                reflection = result,
                timelineViewConstructorBindings = viewBindings,
                timelineViewModelConstructorBindings = viewModelBindings,
                readiness,
                logs = logs.Select(x => new { x.Timestamp, x.Category, x.Message }).ToList(),
            };
            var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var runtime = result.RuntimeKind.ToString();
            var bindingPath = Path.Combine(dir, $"timeline-binding-{runtime}-{stamp}.json");
            var probePath = Path.Combine(dir, $"timeline-probe-{runtime}-{stamp}.json");
            File.WriteAllText(bindingPath, json, Encoding.UTF8);
            File.WriteAllText(probePath, json, Encoding.UTF8);
        }
        catch
        {
            // Diagnostics output failure must not break flow.
        }
    }

    private static void SaveGenerationAttemptDiagnostics(
        YmmTimelineReflectionResult result,
        IReadOnlyList<YmmTimelineConstructorBindingResult> viewBindings,
        IReadOnlyList<YmmTimelineConstructorBindingResult> viewModelBindings,
        YmmTimelineGenerationReadiness? readiness,
        YmmTimelineGenerationAttemptResult? generationAttemptResult,
        IEnumerable<YmmTimelineReflectionLog> logs)
    {
        try
        {
            var dir = Path.Combine(Environment.CurrentDirectory, "logs", "diagnostics");
            Directory.CreateDirectory(dir);
            var output = new
            {
                timestamp = DateTimeOffset.Now,
                runtimeKind = result.RuntimeKind.ToString(),
                processName = result.ProcessName,
                loadedAssemblyCount = result.AssemblyCount,
                ymmAssemblyNames = result.YmmRelatedAssemblyNames,
                reflection = result,
                timelineViewConstructorBindings = viewBindings,
                timelineViewModelConstructorBindings = viewModelBindings,
                readiness,
                generationAttempt = generationAttemptResult,
                logs = logs.Select(x => new { x.Timestamp, x.Category, x.Message }).ToList(),
            };
            var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
            var path = Path.Combine(dir, $"timeline-generation-attempt-{result.RuntimeKind}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, json, Encoding.UTF8);
        }
        catch
        {
            // Diagnostics output failure must not break flow.
        }
    }

    
    public void SaveDiagnosticsSnapshot()
    {
        if (ReflectionResult is null)
        {
            logs.Add(new YmmTimelineReflectionLog { Category = "Diagnostics", Message = "Save skipped: no reflection result." });
            return;
        }

        SaveDiagnostics(
            ReflectionResult,
            TimelineViewBindingResults,
            TimelineViewModelBindingResults,
            GenerationReadiness,
            logs);

        logs.Add(new YmmTimelineReflectionLog
        {
            Category = "Diagnostics",
            Message = $"Diagnostics saved (runtime={ReflectionResult.RuntimeKind}).",
        });
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
            GenerationAttemptResult = null;
            constructorSignatures.Clear();
            constructorBindingSummary.Clear();
            blockingReasons.Clear();
            readinessWarnings.Clear();
            missingDependencies.Clear();
            foundAssemblies.Clear();
            ymmRelatedAssemblies.Clear();
            candidateAssemblies.Clear();
            Status = "Disposed";
            Summary = "Disposed experimental host view model.";
            OnPropertyChanged(nameof(ReflectionResult));
            OnPropertyChanged(nameof(ReflectionSummary));
            OnPropertyChanged(nameof(TimelineViewType));
            OnPropertyChanged(nameof(TimelineViewModelType));
            OnPropertyChanged(nameof(SetTimelineToolInfoOwnerType));
            OnPropertyChanged(nameof(RuntimeKindText));
            OnPropertyChanged(nameof(RuntimeProcessName));
            OnPropertyChanged(nameof(ReadinessScore));
            OnPropertyChanged(nameof(CanAttemptViewModelGeneration));
            OnPropertyChanged(nameof(CanAttemptViewGeneration));
            OnPropertyChanged(nameof(ReadinessSufficient));
            OnPropertyChanged(nameof(GenerationAttempted));
            OnPropertyChanged(nameof(GenerationSucceeded));
            OnPropertyChanged(nameof(GenerationFailureReason));
            OnPropertyChanged(nameof(GenerationConstructorSignature));
            OnPropertyChanged(nameof(GenerationException));
            OnPropertyChanged(nameof(DisposeAttempted));
            OnPropertyChanged(nameof(DisposeSucceeded));
            OnPropertyChanged(nameof(DisposeFailureReason));
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
        YmmRelatedAssemblies = new ReadOnlyObservableCollection<string>(ymmRelatedAssemblies);
        CandidateAssemblies = new ReadOnlyObservableCollection<string>(candidateAssemblies);
    }

    public void RedetectRuntime()
    {
        OnPropertyChanged(nameof(RuntimeKindText));
        OnPropertyChanged(nameof(RuntimeProcessName));
        logs.Add(new YmmTimelineReflectionLog
        {
            Category = "Runtime",
            Message = $"Runtime redetected: {runtimeEnvironmentDetector.Detect()}, process={runtimeEnvironmentDetector.GetProcessName()}",
        });
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





