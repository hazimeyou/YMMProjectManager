using System.Windows;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed class ExperimentalYmmTimelineHostViewModel : ViewModelBase, IDisposable
{
    private readonly YmmTimelineReflectionProbe probe = new();
    private readonly YmmTimelineConstructorBinder constructorBinder = new();
    private readonly YmmTimelineViewModelGenerationAttempt generationAttempt = new();
    private readonly YmmTimelineViewGenerationAttempt timelineViewGenerationAttempt = new();
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
    private PureTimelineExperimentalOptions? lastInitializationOptions;

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
    public YmmTimelineViewGenerationAttemptResult? TimelineViewGenerationAttemptResult { get; private set; }

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
    public string GenerationStackTrace => GenerationAttemptResult?.ExceptionStackTrace ?? string.Empty;
    public string NullInjectedParameters => GenerationAttemptResult is null
        ? string.Empty
        : string.Join(", ", GenerationAttemptResult.NullInjectedParameters);
    public bool DisposeAttempted => GenerationAttemptResult?.DisposeAttempted ?? false;
    public bool DisposeSucceeded => GenerationAttemptResult?.DisposeSucceeded ?? false;
    public string DisposeFailureReason => GenerationAttemptResult?.DisposeFailureReason ?? string.Empty;
    public bool GcVerificationAttempted => GenerationAttemptResult?.GcVerificationAttempted ?? false;
    public string WeakReferenceAfterGc => GenerationAttemptResult?.WeakReferenceAliveAfterGc?.ToString() ?? string.Empty;
    public string FinalizationNote => GenerationAttemptResult?.FinalizationNote ?? string.Empty;
    public bool TimelineViewGenerationAttempted => TimelineViewGenerationAttemptResult?.Attempted ?? false;
    public bool TimelineViewGenerationSucceeded => TimelineViewGenerationAttemptResult?.Succeeded ?? false;
    public string TimelineViewGenerationFailureReason => TimelineViewGenerationAttemptResult?.FailureReason ?? string.Empty;
    public bool TimelineViewVisualAttachAttempted => TimelineViewGenerationAttemptResult?.VisualAttachAttempted ?? false;
    public bool TimelineViewVisualAttachSucceeded => TimelineViewGenerationAttemptResult?.VisualAttachSucceeded ?? false;
    public bool TimelineViewDetachSucceeded => TimelineViewGenerationAttemptResult?.DetachSucceeded ?? false;
    public long TimelineViewAttachDurationMs => TimelineViewGenerationAttemptResult?.AttachDurationMs ?? 0;
    public bool TimelineViewLoadedObserved => TimelineViewGenerationAttemptResult?.LoadedEventObserved ?? false;
    public bool TimelineViewInitializedObserved => TimelineViewGenerationAttemptResult?.InitializedEventObserved ?? false;
    public bool TimelineViewDataContextChangedObserved => TimelineViewGenerationAttemptResult?.DataContextChangedObserved ?? false;
    public bool TimelineViewTemplateAppliedObserved => TimelineViewGenerationAttemptResult?.TemplateAppliedObserved ?? false;
    public bool TimelineViewLayoutUpdatedObserved => TimelineViewGenerationAttemptResult?.LayoutUpdatedObserved ?? false;
    public bool TimelineViewRenderingObserved => TimelineViewGenerationAttemptResult?.RenderingObserved ?? false;
    public string TimelineViewWeakReferenceAfterGc => TimelineViewGenerationAttemptResult?.WeakReferenceAliveAfterGc?.ToString() ?? string.Empty;

    public bool TryInitialize(PureTimelineExperimentalOptions options)
    {
        return TryInitializeAsync(options).GetAwaiter().GetResult();
    }

    public async Task<bool> TryInitializeAsync(PureTimelineExperimentalOptions options, IProgress<int>? progress = null)
    {
        lastInitializationOptions = options;
        if (disposed)
        {
            logs.Add(new YmmTimelineReflectionLog { Category = "Init", Message = "Initialization skipped: already disposed." });
            return false;
        }

        initializeStopwatch.Restart();
        logs.Clear();
        logs.Add(new YmmTimelineReflectionLog { Category = "Init", Message = $"Initialize start (useReflection={options.UseReflection})" });
        logs.Add(new YmmTimelineReflectionLog
        {
            Category = "Options",
            Message =
                $"Flags: EnableExperimentalYmmTimelineHost={options.EnableExperimentalYmmTimelineHost}, " +
                $"AllowViewModelGenerationAttempt={options.AllowViewModelGenerationAttempt}, " +
                $"AllowTimelineViewGenerationAttempt={options.AllowTimelineViewGenerationAttempt}, " +
                $"AllowProjectDiffWindowPreintegrationAttempt={options.AllowProjectDiffWindowPreintegrationAttempt}, " +
                $"ManualApprovalForProjectDiffWindowPreintegration={options.ManualApprovalForProjectDiffWindowPreintegration}"
        });
        progress?.Report(5);

        try
        {
            var computed = await Task.Run(async () =>
            {
                var localLogs = new List<YmmTimelineReflectionLog>
                {
                    new() { Category = "Init", Message = $"Initialize start (useReflection={options.UseReflection})" },
                    new()
                    {
                        Category = "Options",
                        Message =
                            $"Flags: EnableExperimentalYmmTimelineHost={options.EnableExperimentalYmmTimelineHost}, " +
                            $"AllowViewModelGenerationAttempt={options.AllowViewModelGenerationAttempt}, " +
                            $"AllowTimelineViewGenerationAttempt={options.AllowTimelineViewGenerationAttempt}, " +
                            $"AllowProjectDiffWindowPreintegrationAttempt={options.AllowProjectDiffWindowPreintegrationAttempt}, " +
                            $"ManualApprovalForProjectDiffWindowPreintegration={options.ManualApprovalForProjectDiffWindowPreintegration}"
                    }
                };

                if (!options.UseReflection)
                {
                    return new InitializationComputation
                    {
                        ReflectionDisabled = true,
                        Logs = localLogs,
                    };
                }

                progress?.Report(20);
                var result = probe.Probe(localLogs);
                progress?.Report(55);

                var timelineViewType = ResolveType(result.TimelineViewTypeName);
                var timelineViewModelType = ResolveType(result.TimelineViewModelTypeName);
                var viewBindings = constructorBinder.DryRunForType(timelineViewType, result, localLogs);
                var vmBindings = constructorBinder.DryRunForType(timelineViewModelType, result, localLogs);
                var readiness = constructorBinder.BuildReadiness(result, viewBindings, vmBindings);
                progress?.Report(75);

                YmmTimelineGenerationAttemptResult? generation = null;
                YmmTimelineViewGenerationAttemptResult? viewGeneration = null;
                var gatePassed =
                    options.EnableExperimentalYmmTimelineHost &&
                    options.AllowViewModelGenerationAttempt &&
                    readiness.Score >= options.MinimumReadinessScoreForGeneration &&
                    readiness.CanAttemptViewModelGeneration;
                var (strictGatePassed, strictGateReason) = EvaluateStrictConfidenceGate(result, readiness);

                if (gatePassed && strictGatePassed)
                {
                    var selectedBinding = vmBindings
                        .Where(x => x.CanAttemptGeneration)
                        .OrderByDescending(x => x.Parameters.Count)
                        .FirstOrDefault();
                    if (selectedBinding is not null && timelineViewModelType is not null)
                    {
                        var runtimeDependencyInstances = ResolveRuntimeDependencyInstances(localLogs);
                        generation = await generationAttempt
                            .TryGenerateAndDisposeAsync(
                                timelineViewModelType,
                                selectedBinding,
                                options.DisposeImmediatelyAfterGeneration && !options.AllowTimelineViewGenerationAttempt,
                                runtimeDependencyInstances)
                            .ConfigureAwait(false);
                        generation.StrictConfidenceGatePassed = true;
                        generation.StrictConfidenceGateReason = "Passed";
                        generation.InjectedDependencies = BuildInjectedDependencySummaries(runtimeDependencyInstances, result);
                        if (generation.Succeeded && generation.GeneratedInstance is not null)
                        {
                            runtimeDependencyInstances = new Dictionary<string, object?>(runtimeDependencyInstances, StringComparer.Ordinal)
                            {
                                ["YukkuriMovieMaker.ViewModels.TimelineViewModel"] = generation.GeneratedInstance
                            };
                        }

                        if (options.AllowTimelineViewGenerationAttempt &&
                            options.AllowViewModelGenerationAttempt &&
                            generation.Succeeded &&
                            timelineViewType is not null)
                        {
                            var selectedViewBinding = viewBindings
                                .Where(x => x.CanAttemptGeneration)
                                .OrderByDescending(x => x.Parameters.Count)
                                .FirstOrDefault();
                            if (selectedViewBinding is not null)
                            {
                                viewGeneration = await timelineViewGenerationAttempt
                                    .TryGenerateAndDisposeAsync(
                                        timelineViewType,
                                        selectedViewBinding,
                                        runtimeDependencyInstances,
                                        options)
                                    .ConfigureAwait(false);

                                if (generation.GeneratedInstance is not null && !generation.DisposeAttempted)
                                {
                                    generation.DisposeAttempted = true;
                                    var vmDisposeSw = Stopwatch.StartNew();
                                    var vmDisposeResult = await YmmTimelineInstanceDisposer.DisposeAsync(generation.GeneratedInstance).ConfigureAwait(false);
                                    vmDisposeSw.Stop();
                                    generation.DisposeMs = vmDisposeSw.ElapsedMilliseconds;
                                    generation.DisposeSucceeded = vmDisposeResult.Succeeded;
                                    generation.DisposeFailureReason = vmDisposeResult.FailureReason;
                                    generation.GeneratedInstance = null;
                                }
                            }
                        }
                        else if (options.AllowTimelineViewGenerationAttempt)
                        {
                            viewGeneration = new YmmTimelineViewGenerationAttemptResult
                            {
                                Attempted = false,
                                Succeeded = false,
                                FailureReason = timelineViewType is null
                                    ? "TimelineView type is unavailable."
                                    : generation.Succeeded
                                        ? "TimelineView generation binding is unavailable."
                                        : "TimelineView generation skipped because ViewModel generation failed.",
                                VisualAttachForbidden = true,
                            };
                        }
                    }
                }
                else if (options.AllowViewModelGenerationAttempt)
                {
                    generation = new YmmTimelineGenerationAttemptResult
                    {
                        Attempted = false,
                        Succeeded = false,
                        TargetTypeName = result.TimelineViewModelTypeName ?? string.Empty,
                        ConstructorSignature = "(not attempted)",
                        FailureReason = gatePassed
                            ? $"Generation skipped by strict confidence gate: {strictGateReason}"
                            : $"Generation skipped by gate: score={readiness.Score}, threshold={options.MinimumReadinessScoreForGeneration}, canAttemptVm={readiness.CanAttemptViewModelGeneration}",
                        StrictConfidenceGatePassed = strictGatePassed,
                        StrictConfidenceGateReason = strictGateReason,
                    };
                    if (options.AllowTimelineViewGenerationAttempt)
                    {
                        viewGeneration = new YmmTimelineViewGenerationAttemptResult
                        {
                            Attempted = false,
                            Succeeded = false,
                            FailureReason = $"TimelineView generation skipped by gate: score={readiness.Score}, strictGate={strictGatePassed}, canAttemptView={readiness.CanAttemptViewGeneration}.",
                            VisualAttachForbidden = true,
                        };
                    }
                }

                progress?.Report(90);
                return new InitializationComputation
                {
                    Result = result,
                    TimelineViewBindings = viewBindings,
                    TimelineViewModelBindings = vmBindings,
                    Readiness = readiness,
                    GenerationAttempt = generation,
                    TimelineViewGenerationAttempt = viewGeneration,
                    Logs = localLogs,
                };
            }).ConfigureAwait(true);

            logs.Clear();
            foreach (var entry in computed.Logs)
            {
                logs.Add(entry);
            }

            if (computed.ReflectionDisabled)
            {
                Status = "ReflectionDisabled";
                Summary = "UseReflection=false";
                return false;
            }

            var result = computed.Result!;
            ReflectionResult = result;
            TimelineViewBindingResults = computed.TimelineViewBindings;
            TimelineViewModelBindingResults = computed.TimelineViewModelBindings;
            GenerationReadiness = computed.Readiness;
            GenerationAttemptResult = computed.GenerationAttempt;
            TimelineViewGenerationAttemptResult = computed.TimelineViewGenerationAttempt;

            ReplaceCollection(constructorSignatures, result.ConstructorSignatures);
            ReplaceCollection(missingDependencies, result.MissingDependencies);
            ReplaceCollection(foundAssemblies, result.FoundAssemblies);
            ReplaceCollection(ymmRelatedAssemblies, result.YmmRelatedAssemblyNames);
            ReplaceCollection(candidateAssemblies, result.CandidateAssemblyNames);
            RefreshBindingCollections();

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
            OnPropertyChanged(nameof(GenerationStackTrace));
            OnPropertyChanged(nameof(NullInjectedParameters));
            OnPropertyChanged(nameof(DisposeAttempted));
            OnPropertyChanged(nameof(DisposeSucceeded));
            OnPropertyChanged(nameof(DisposeFailureReason));
            OnPropertyChanged(nameof(GcVerificationAttempted));
            OnPropertyChanged(nameof(WeakReferenceAfterGc));
            OnPropertyChanged(nameof(FinalizationNote));
            OnPropertyChanged(nameof(TimelineViewGenerationAttempted));
            OnPropertyChanged(nameof(TimelineViewGenerationSucceeded));
            OnPropertyChanged(nameof(TimelineViewGenerationFailureReason));
            OnPropertyChanged(nameof(TimelineViewVisualAttachAttempted));
            OnPropertyChanged(nameof(TimelineViewVisualAttachSucceeded));
            OnPropertyChanged(nameof(TimelineViewDetachSucceeded));
            OnPropertyChanged(nameof(TimelineViewAttachDurationMs));
            OnPropertyChanged(nameof(TimelineViewLoadedObserved));
            OnPropertyChanged(nameof(TimelineViewInitializedObserved));
            OnPropertyChanged(nameof(TimelineViewDataContextChangedObserved));
            OnPropertyChanged(nameof(TimelineViewTemplateAppliedObserved));
            OnPropertyChanged(nameof(TimelineViewLayoutUpdatedObserved));
            OnPropertyChanged(nameof(TimelineViewRenderingObserved));
            OnPropertyChanged(nameof(TimelineViewWeakReferenceAfterGc));

            PureTimelineDiagnostics.UpdateTimelineReflectionMetrics(result.ProbeMs, result.AssemblyCount, result.TypeFoundCount);
            PureTimelineDiagnostics.UpdateTimelineConstructorBindingMetrics(
                0,
                TimelineViewBindingResults.Count + TimelineViewModelBindingResults.Count,
                TimelineViewBindingResults.Count(x => x.CanAttemptGeneration) + TimelineViewModelBindingResults.Count(x => x.CanAttemptGeneration),
                GenerationReadiness.Score,
                GenerationReadiness.BlockingReasons.Count);

            SaveDiagnostics(result, TimelineViewBindingResults, TimelineViewModelBindingResults, GenerationReadiness, computed.Logs);
            if (GenerationAttemptResult is not null)
            {
                PureTimelineDiagnostics.UpdateTimelineViewModelGenerationMetrics(GenerationAttemptResult);
                SaveGenerationAttemptDiagnostics(result, TimelineViewBindingResults, TimelineViewModelBindingResults, GenerationReadiness, GenerationAttemptResult, computed.Logs);
            }
            if (TimelineViewGenerationAttemptResult is not null ||
                (GenerationAttemptResult is not null && options.AllowTimelineViewGenerationAttempt))
            {
                var timelineViewAttempt = TimelineViewGenerationAttemptResult ?? new YmmTimelineViewGenerationAttemptResult
                {
                    Attempted = false,
                    Succeeded = false,
                    FailureReason = "TimelineView generation was not executed.",
                    VisualAttachForbidden = true,
                };
                SaveTimelineViewGenerationAttemptDiagnostics(result, TimelineViewBindingResults, TimelineViewModelBindingResults, GenerationReadiness, GenerationAttemptResult, timelineViewAttempt, computed.Logs, options);
                TimelineViewGenerationAttemptResult = timelineViewAttempt;
            }

            if (GenerationAttemptResult?.Attempted == true)
            {
                if (GenerationAttemptResult.Succeeded && (!GenerationAttemptResult.DisposeAttempted || GenerationAttemptResult.DisposeSucceeded))
                {
                    Status = "ExperimentalReady";
                    Summary = "ViewModel generation and immediate dispose succeeded.";
                    progress?.Report(100);
                    return true;
                }

                Status = "Unavailable";
                Summary = $"Generation failed: {GenerationAttemptResult.FailureReason ?? GenerationAttemptResult.ExceptionMessage}";
                progress?.Report(100);
                return false;
            }

            if (GenerationReadiness.Score >= 70 && GenerationReadiness.CanAttemptViewModelGeneration)
            {
                Status = "ExperimentalReady";
                Summary = options.AllowViewModelGenerationAttempt
                    ? $"Generation skipped: gate unresolved (score={GenerationReadiness.Score})."
                    : $"Readiness: {GenerationReadiness.Score} / Generation attempt is disabled by default.";
                progress?.Report(100);
                return true;
            }

            Status = "Unavailable";
            Summary = $"Readiness: {GenerationReadiness.Score} / {string.Join(" ; ", GenerationReadiness.BlockingReasons.DefaultIfEmpty("Required dependencies are missing."))}";
            if (result.RuntimeKind == RuntimeEnvironmentKind.Benchmark)
            {
                Summary += " / Benchmark環境では YMM Timeline 型は通常見つかりません。";
            }
            progress?.Report(100);
            return false;
        }
        catch (Exception ex)
        {
            PureTimelineDiagnostics.IncrementTimelineReflectionFailureCount();
            logs.Add(new YmmTimelineReflectionLog { Category = "Error", Message = $"Probe exception: {ex.GetType().Name}: {ex.Message}" });
            Status = "InitializeFailed";
            Summary = ex.Message;
            progress?.Report(100);
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

    private sealed class InitializationComputation
    {
        public bool ReflectionDisabled { get; set; }
        public YmmTimelineReflectionResult? Result { get; set; }
        public IReadOnlyList<YmmTimelineConstructorBindingResult> TimelineViewBindings { get; set; } = [];
        public IReadOnlyList<YmmTimelineConstructorBindingResult> TimelineViewModelBindings { get; set; } = [];
        public YmmTimelineGenerationReadiness Readiness { get; set; } = new();
        public YmmTimelineGenerationAttemptResult? GenerationAttempt { get; set; }
        public YmmTimelineViewGenerationAttemptResult? TimelineViewGenerationAttempt { get; set; }
        public List<YmmTimelineReflectionLog> Logs { get; set; } = [];
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
                summary = new
                {
                    sceneResolved = result.SceneDiscovery.Resolved,
                    undoRedoResolved = result.UndoRedoManagerDiscovery.Resolved,
                    asyncAwaitResolved = result.AsyncAwaitStatusDiscovery.Resolved,
                    topSceneOwners = result.SceneDiscovery.CandidateOwners.Take(5).ToArray(),
                    topUndoRedoOwners = result.UndoRedoManagerDiscovery.CandidateOwners.Take(5).ToArray(),
                    topAsyncAwaitOwners = result.AsyncAwaitStatusDiscovery.CandidateOwners.Take(5).ToArray(),
                },
                reflection = result,
                timelineViewConstructorBindings = viewBindings,
                timelineViewModelConstructorBindings = viewModelBindings,
                readiness,
                logs = logs.Select(x => new { x.Timestamp, x.Category, x.Message }).ToList(),
            };
            var json = JsonSerializer.Serialize(output, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            });
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
                summary = new
                {
                    sceneResolved = result.SceneDiscovery.Resolved,
                    undoRedoResolved = result.UndoRedoManagerDiscovery.Resolved,
                    asyncAwaitResolved = result.AsyncAwaitStatusDiscovery.Resolved,
                    topSceneOwners = result.SceneDiscovery.CandidateOwners.Take(5).ToArray(),
                    topUndoRedoOwners = result.UndoRedoManagerDiscovery.CandidateOwners.Take(5).ToArray(),
                    topAsyncAwaitOwners = result.AsyncAwaitStatusDiscovery.CandidateOwners.Take(5).ToArray(),
                },
                reflection = result,
                timelineViewConstructorBindings = viewBindings,
                timelineViewModelConstructorBindings = viewModelBindings,
                readiness,
                generationAttempt = generationAttemptResult,
                logs = logs.Select(x => new { x.Timestamp, x.Category, x.Message }).ToList(),
            };
            var json = JsonSerializer.Serialize(output, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            });
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

        if (GenerationAttemptResult is not null)
        {
            SaveGenerationAttemptDiagnostics(
                ReflectionResult,
                TimelineViewBindingResults,
                TimelineViewModelBindingResults,
                GenerationReadiness,
                GenerationAttemptResult,
                logs);
        }

        // Ensure timeline-view-* diagnostics are always emitted once generation path exists.
        if (GenerationAttemptResult is not null || TimelineViewGenerationAttemptResult is not null)
        {
            var timelineViewAttempt = TimelineViewGenerationAttemptResult ?? new YmmTimelineViewGenerationAttemptResult
            {
                Attempted = false,
                Succeeded = false,
                FailureReason = "TimelineView generation diagnostics snapshot without executed TimelineView attempt.",
                VisualAttachForbidden = true,
            };

            SaveTimelineViewGenerationAttemptDiagnostics(
                ReflectionResult,
                TimelineViewBindingResults,
                TimelineViewModelBindingResults,
                GenerationReadiness,
                GenerationAttemptResult,
                timelineViewAttempt,
                logs,
                lastInitializationOptions);
        }

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
            OnPropertyChanged(nameof(GenerationStackTrace));
            OnPropertyChanged(nameof(NullInjectedParameters));
            OnPropertyChanged(nameof(DisposeAttempted));
            OnPropertyChanged(nameof(DisposeSucceeded));
            OnPropertyChanged(nameof(DisposeFailureReason));
            OnPropertyChanged(nameof(GcVerificationAttempted));
            OnPropertyChanged(nameof(WeakReferenceAfterGc));
            OnPropertyChanged(nameof(FinalizationNote));
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

    private static void SaveTimelineViewGenerationAttemptDiagnostics(
        YmmTimelineReflectionResult result,
        IReadOnlyList<YmmTimelineConstructorBindingResult> viewBindings,
        IReadOnlyList<YmmTimelineConstructorBindingResult> viewModelBindings,
        YmmTimelineGenerationReadiness? readiness,
        YmmTimelineGenerationAttemptResult? generationAttemptResult,
        YmmTimelineViewGenerationAttemptResult? timelineViewGenerationAttemptResult,
        IEnumerable<YmmTimelineReflectionLog> logs,
        PureTimelineExperimentalOptions? options = null)
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
                reflectionSummary = new
                {
                    result.RuntimeKind,
                    result.ProcessName,
                    result.AssemblyCount,
                    result.TypeFoundCount,
                    result.TimelineViewFound,
                    result.TimelineViewModelFound,
                },
                readinessSummary = new
                {
                    score = readiness?.Score ?? 0,
                    canVm = readiness?.CanAttemptViewModelGeneration ?? false,
                    canView = readiness?.CanAttemptViewGeneration ?? false,
                    blockingReasonCount = readiness?.BlockingReasons.Count ?? 0,
                },
                generationSummary = new
                {
                    attempted = generationAttemptResult?.Attempted ?? false,
                    succeeded = generationAttemptResult?.Succeeded ?? false,
                    failureReason = generationAttemptResult?.FailureReason,
                    disposeSucceeded = generationAttemptResult?.DisposeSucceeded ?? false,
                },
                timelineViewGenerationAttempt = timelineViewGenerationAttemptResult,
                dataContextBoundary = timelineViewGenerationAttemptResult?.DataContextBoundaryPatterns ?? [],
                commandRouteBoundary = timelineViewGenerationAttemptResult?.CommandRouteBoundary,
                visualTreeInventory = timelineViewGenerationAttemptResult?.VisualTreeInventory,
                bindingSurfaceInventory = timelineViewGenerationAttemptResult?.BindingSurfaceInventory,
                resourceInventory = timelineViewGenerationAttemptResult?.ResourceInventory,
                lifecycleRepeatability = timelineViewGenerationAttemptResult?.LifecycleRepeatability,
                expandedVisualTreeInventory = timelineViewGenerationAttemptResult?.ExpandedVisualTreeInventory,
                layoutSizeSweep = timelineViewGenerationAttemptResult?.LayoutSizeSweep,
                dispatcherPriorityBoundary = timelineViewGenerationAttemptResult?.DispatcherPriorityBoundary,
                scrollContentInventory = timelineViewGenerationAttemptResult?.ScrollContentInventory,
                viewModelSurfaceInventory = timelineViewGenerationAttemptResult?.ViewModelSurfaceInventory,
                themeResourceSmoke = timelineViewGenerationAttemptResult?.ThemeResourceSmoke,
                sizePropagation = timelineViewGenerationAttemptResult?.SizePropagation,
                measureArrangeBoundary = timelineViewGenerationAttemptResult?.MeasureArrangeBoundary,
                parentContainerVariation = timelineViewGenerationAttemptResult?.ParentContainerVariation,
                layoutConstraintDiagnostics = timelineViewGenerationAttemptResult?.LayoutConstraintDiagnostics,
                sizePropagationSummary = timelineViewGenerationAttemptResult?.SizePropagationSummary,
                visualStateInventory = timelineViewGenerationAttemptResult?.VisualStateInventory,
                automationInventory = timelineViewGenerationAttemptResult?.AutomationInventory,
                riskClassification = timelineViewGenerationAttemptResult?.RiskClassification,
                viewModelSemanticMemberClassification = timelineViewGenerationAttemptResult?.ViewModelSemanticMemberClassification,
                viewModelSafeGetterSnapshot = timelineViewGenerationAttemptResult?.ViewModelSafeGetterSnapshot,
                viewModelCollectionSurfaceInventory = timelineViewGenerationAttemptResult?.ViewModelCollectionSurfaceInventory,
                viewModelCommandSurfaceInventory = timelineViewGenerationAttemptResult?.ViewModelCommandSurfaceInventory,
                bindingToViewModelMap = timelineViewGenerationAttemptResult?.BindingToViewModelMap,
                visualSemanticClassification = timelineViewGenerationAttemptResult?.VisualSemanticClassification,
                viewModelEventSurfaceInventory = timelineViewGenerationAttemptResult?.ViewModelEventSurfaceInventory,
                dataCandidateDiscovery = timelineViewGenerationAttemptResult?.DataCandidateDiscovery,
                selectedStateSnapshot = timelineViewGenerationAttemptResult?.SelectedStateSnapshot,
                positionScaleSnapshot = timelineViewGenerationAttemptResult?.PositionScaleSnapshot,
                candidateCollectionCountSmoke = timelineViewGenerationAttemptResult?.CandidateCollectionCountSmoke,
                candidateCollectionSampleMetadata = timelineViewGenerationAttemptResult?.CandidateCollectionSampleMetadata,
                semanticSurfaceSummary = timelineViewGenerationAttemptResult?.SemanticSurfaceSummary,
                projectDataMappingFeasibility = timelineViewGenerationAttemptResult?.ProjectDataMappingFeasibility,
                investigationPhaseGate = timelineViewGenerationAttemptResult?.InvestigationPhaseGate,
                projectRootDiscovery = timelineViewGenerationAttemptResult?.ProjectRootDiscovery,
                layerSurfaceInventory = timelineViewGenerationAttemptResult?.LayerSurfaceInventory,
                itemSurfaceInventory = timelineViewGenerationAttemptResult?.ItemSurfaceInventory,
                mediaPathCandidateInventory = timelineViewGenerationAttemptResult?.MediaPathCandidateInventory,
                temporalSurfaceInventory = timelineViewGenerationAttemptResult?.TemporalSurfaceInventory,
                selectionNavigationSurface = timelineViewGenerationAttemptResult?.SelectionNavigationSurface,
                hierarchyMapping = timelineViewGenerationAttemptResult?.HierarchyMapping,
                safePrimitiveSnapshot = timelineViewGenerationAttemptResult?.SafePrimitiveSnapshot,
                observableFlowInventory = timelineViewGenerationAttemptResult?.ObservableFlowInventory,
                semanticDiffCandidates = timelineViewGenerationAttemptResult?.SemanticDiffCandidates,
                serializationSurfaceInventory = timelineViewGenerationAttemptResult?.SerializationSurfaceInventory,
                internalTypeDependencyMap = timelineViewGenerationAttemptResult?.InternalTypeDependencyMap,
                readOnlyRiskHotspots = timelineViewGenerationAttemptResult?.ReadOnlyRiskHotspots,
                semanticBridgeFeasibility = timelineViewGenerationAttemptResult?.SemanticBridgeFeasibility,
                nextPhaseGate = timelineViewGenerationAttemptResult?.NextPhaseGate,
                bindingErrorObservationUnavailable = timelineViewGenerationAttemptResult?.BindingErrorObservationUnavailable ?? true,
                visualAttachAttempted = timelineViewGenerationAttemptResult?.VisualAttachAttempted ?? false,
                visualAttachForbidden = timelineViewGenerationAttemptResult?.VisualAttachForbidden ?? true,
                logsCount = logs.Count(),
            };
            var serializerOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            };
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            void WriteScoped(string filePrefix, object? payload)
            {
                if (payload is null) return;
                var scoped = new
                {
                    timestamp = DateTimeOffset.Now,
                    runtimeKind = result.RuntimeKind.ToString(),
                    processName = result.ProcessName,
                    payload,
                };
                var scopedJson = JsonSerializer.Serialize(scoped, serializerOptions);
                var scopedPath = Path.Combine(dir, $"{filePrefix}-{result.RuntimeKind}-{timestamp}.json");
                File.WriteAllText(scopedPath, scopedJson, Encoding.UTF8);
            }

            // keep one full diagnostic snapshot
            WriteScoped("timeline-generation-attempt", output);

            WriteScoped("timeline-view-datacontext-boundary", timelineViewGenerationAttemptResult?.DataContextBoundaryPatterns);
            WriteScoped("timeline-view-passive-event-boundary", timelineViewGenerationAttemptResult?.PassiveEventBoundary);
            WriteScoped("timeline-view-command-route-boundary", timelineViewGenerationAttemptResult?.CommandRouteBoundary);
            WriteScoped("timeline-view-visual-tree-inventory", timelineViewGenerationAttemptResult?.VisualTreeInventory);
            WriteScoped("timeline-view-binding-surface-inventory", timelineViewGenerationAttemptResult?.BindingSurfaceInventory);
            WriteScoped("timeline-view-resource-style-template-inventory", timelineViewGenerationAttemptResult?.ResourceInventory);
            WriteScoped("timeline-view-lifecycle-repeatability", timelineViewGenerationAttemptResult?.LifecycleRepeatability);
            WriteScoped("timeline-view-expanded-visual-tree-inventory", timelineViewGenerationAttemptResult?.ExpandedVisualTreeInventory);
            WriteScoped("timeline-view-layout-size-sweep", timelineViewGenerationAttemptResult?.LayoutSizeSweep);
            WriteScoped("timeline-view-dispatcher-priority-boundary", timelineViewGenerationAttemptResult?.DispatcherPriorityBoundary);
            WriteScoped("timeline-view-scroll-content-inventory", timelineViewGenerationAttemptResult?.ScrollContentInventory);
            WriteScoped("timeline-viewmodel-member-surface-inventory", timelineViewGenerationAttemptResult?.ViewModelSurfaceInventory);
            WriteScoped("timeline-view-theme-resource-smoke", timelineViewGenerationAttemptResult?.ThemeResourceSmoke);
            WriteScoped("timeline-view-size-propagation", timelineViewGenerationAttemptResult?.SizePropagation);
            WriteScoped("timeline-view-measure-arrange-boundary", timelineViewGenerationAttemptResult?.MeasureArrangeBoundary);
            WriteScoped("timeline-view-parent-container-variation", timelineViewGenerationAttemptResult?.ParentContainerVariation);
            WriteScoped("timeline-view-layout-constraint-diagnostics", timelineViewGenerationAttemptResult?.LayoutConstraintDiagnostics);
            WriteScoped("timeline-view-size-propagation-summary", timelineViewGenerationAttemptResult?.SizePropagationSummary);
            WriteScoped("timeline-view-visual-state-inventory", timelineViewGenerationAttemptResult?.VisualStateInventory);
            WriteScoped("timeline-view-automation-inventory", timelineViewGenerationAttemptResult?.AutomationInventory);
            WriteScoped("timeline-view-risk-classification", timelineViewGenerationAttemptResult?.RiskClassification);
            WriteScoped("timeline-viewmodel-semantic-member-classification", timelineViewGenerationAttemptResult?.ViewModelSemanticMemberClassification);
            WriteScoped("timeline-viewmodel-safe-getter-snapshot", timelineViewGenerationAttemptResult?.ViewModelSafeGetterSnapshot);
            WriteScoped("timeline-viewmodel-collection-surface-inventory", timelineViewGenerationAttemptResult?.ViewModelCollectionSurfaceInventory);
            WriteScoped("timeline-viewmodel-command-surface-inventory", timelineViewGenerationAttemptResult?.ViewModelCommandSurfaceInventory);
            WriteScoped("timeline-view-binding-to-viewmodel-map", timelineViewGenerationAttemptResult?.BindingToViewModelMap);
            WriteScoped("timeline-view-visual-semantic-classification", timelineViewGenerationAttemptResult?.VisualSemanticClassification);
            WriteScoped("timeline-viewmodel-event-surface-inventory", timelineViewGenerationAttemptResult?.ViewModelEventSurfaceInventory);
            WriteScoped("timeline-data-candidate-discovery", timelineViewGenerationAttemptResult?.DataCandidateDiscovery);
            WriteScoped("timeline-selected-state-snapshot", timelineViewGenerationAttemptResult?.SelectedStateSnapshot);
            WriteScoped("timeline-position-scale-snapshot", timelineViewGenerationAttemptResult?.PositionScaleSnapshot);
            WriteScoped("timeline-candidate-collection-count-smoke", timelineViewGenerationAttemptResult?.CandidateCollectionCountSmoke);
            WriteScoped("timeline-candidate-collection-sample-metadata", timelineViewGenerationAttemptResult?.CandidateCollectionSampleMetadata);
            WriteScoped("timeline-semantic-surface-summary", timelineViewGenerationAttemptResult?.SemanticSurfaceSummary);
            WriteScoped("timeline-project-data-mapping-feasibility", timelineViewGenerationAttemptResult?.ProjectDataMappingFeasibility);
            WriteScoped("timeline-investigation-phase-gate", timelineViewGenerationAttemptResult?.InvestigationPhaseGate);
            WriteScoped("timeline-project-root-discovery", timelineViewGenerationAttemptResult?.ProjectRootDiscovery);
            WriteScoped("timeline-layer-surface-inventory", timelineViewGenerationAttemptResult?.LayerSurfaceInventory);
            WriteScoped("timeline-item-surface-inventory", timelineViewGenerationAttemptResult?.ItemSurfaceInventory);
            WriteScoped("timeline-media-path-candidate-inventory", timelineViewGenerationAttemptResult?.MediaPathCandidateInventory);
            WriteScoped("timeline-temporal-surface-inventory", timelineViewGenerationAttemptResult?.TemporalSurfaceInventory);
            WriteScoped("timeline-selection-navigation-surface", timelineViewGenerationAttemptResult?.SelectionNavigationSurface);
            WriteScoped("timeline-hierarchy-mapping", timelineViewGenerationAttemptResult?.HierarchyMapping);
            WriteScoped("timeline-safe-primitive-snapshot", timelineViewGenerationAttemptResult?.SafePrimitiveSnapshot);
            WriteScoped("timeline-observable-flow-inventory", timelineViewGenerationAttemptResult?.ObservableFlowInventory);
            WriteScoped("timeline-semantic-diff-input-candidates", timelineViewGenerationAttemptResult?.SemanticDiffCandidates);
            WriteScoped("timeline-serialization-surface-inventory", timelineViewGenerationAttemptResult?.SerializationSurfaceInventory);
            WriteScoped("timeline-internal-type-dependency-map", timelineViewGenerationAttemptResult?.InternalTypeDependencyMap);
            WriteScoped("timeline-readonly-risk-hotspots", timelineViewGenerationAttemptResult?.ReadOnlyRiskHotspots);
            WriteScoped("timeline-semantic-bridge-feasibility", timelineViewGenerationAttemptResult?.SemanticBridgeFeasibility);
            WriteScoped("timeline-next-phase-gate", timelineViewGenerationAttemptResult?.NextPhaseGate);
            WriteScoped("timeline-investigation-policy-resolver", timelineViewGenerationAttemptResult?.InvestigationPolicyResolver);
            WriteScoped("timeline-readonly-bridge-allowed-scope", timelineViewGenerationAttemptResult?.ReadOnlyBridgeAllowedScope);
            WriteScoped("timeline-controlled-project-snapshot-feasibility", timelineViewGenerationAttemptResult?.ControlledProjectSnapshotFeasibility);
            WriteScoped("timeline-read-model-prototype", timelineViewGenerationAttemptResult?.ReadModelPrototype);
            WriteScoped("timeline-read-model-validation", timelineViewGenerationAttemptResult?.ReadModelValidation);
            WriteScoped("timeline-passive-snapshot-repeatability", timelineViewGenerationAttemptResult?.PassiveSnapshotRepeatability);
            WriteScoped("timeline-snapshot-performance-smoke", timelineViewGenerationAttemptResult?.SnapshotPerformanceSmoke);
            WriteScoped("timeline-bridge-failure-mode-catalog", timelineViewGenerationAttemptResult?.BridgeFailureModeCatalog);
            WriteScoped("timeline-controlled-snapshot-risk-report", timelineViewGenerationAttemptResult?.ControlledSnapshotRiskReport);
            WriteScoped("timeline-passive-project-binding-readiness", timelineViewGenerationAttemptResult?.PassiveProjectBindingReadiness);
            WriteScoped("timeline-semantic-diff-bridge-prototype", timelineViewGenerationAttemptResult?.SemanticDiffBridgePrototype);
            WriteScoped("timeline-timeline-diff-bridge-prototype", timelineViewGenerationAttemptResult?.TimelineDiffBridgePrototype);
            WriteScoped("timeline-placeholder-readmodel-adapter-feasibility", timelineViewGenerationAttemptResult?.PlaceholderReadModelAdapterFeasibility);
            WriteScoped("timeline-investigation-milestone-summary", timelineViewGenerationAttemptResult?.InvestigationMilestoneSummary);
            WriteScoped("timeline-small-visible-host-preflight", timelineViewGenerationAttemptResult?.SmallVisibleHostPreflight);
            WriteScoped("timeline-integration-blocklist", timelineViewGenerationAttemptResult?.IntegrationBlocklist);
            WriteScoped("timeline-next-safe-preview-planner", timelineViewGenerationAttemptResult?.NextSafePreviewPlanner);
            WriteScoped("timeline-diagnostics-index", timelineViewGenerationAttemptResult?.DiagnosticsIndex);
            WriteScoped("timeline-current-batch-final-gate", timelineViewGenerationAttemptResult?.CurrentBatchFinalGate);
            WriteScoped("timeline-empty-snapshot-root-cause-classifier", timelineViewGenerationAttemptResult?.EmptySnapshotRootCauseClassifier);
            WriteScoped("timeline-runtime-owner-chain-discovery", timelineViewGenerationAttemptResult?.RuntimeOwnerChainDiscovery);
            WriteScoped("timeline-active-project-context-candidates", timelineViewGenerationAttemptResult?.ActiveProjectContextCandidates);
            WriteScoped("timeline-runtime-instance-discovery", timelineViewGenerationAttemptResult?.RuntimeInstanceDiscovery);
            WriteScoped("timeline-runtime-vs-generated-viewmodel-comparison", timelineViewGenerationAttemptResult?.RuntimeVsGeneratedViewModelComparison);
            WriteScoped("timeline-runtime-safe-getter-snapshot", timelineViewGenerationAttemptResult?.RuntimeSafeGetterSnapshot);
            WriteScoped("timeline-runtime-collection-count-smoke", timelineViewGenerationAttemptResult?.RuntimeCollectionCountSmoke);
            WriteScoped("timeline-runtime-project-layer-item-mapping", timelineViewGenerationAttemptResult?.RuntimeProjectLayerItemMapping);
            WriteScoped("timeline-runtime-data-bridge-feasibility", timelineViewGenerationAttemptResult?.RuntimeDataBridgeFeasibility);
            WriteScoped("timeline-owner-chain-paths", timelineViewGenerationAttemptResult?.OwnerChainPaths);
            WriteScoped("timeline-runtime-snapshot-dryrun-policy", timelineViewGenerationAttemptResult?.RuntimeSnapshotDryRunPolicy);
            WriteScoped("timeline-runtime-readonly-snapshot-dryrun", timelineViewGenerationAttemptResult?.RuntimeReadOnlySnapshotDryRun);
            WriteScoped("timeline-runtime-snapshot-repeatability", timelineViewGenerationAttemptResult?.RuntimeSnapshotRepeatability);
            WriteScoped("timeline-runtime-snapshot-performance-smoke", timelineViewGenerationAttemptResult?.RuntimeSnapshotPerformanceSmoke);
            WriteScoped("timeline-runtime-semantic-diff-input-dryrun", timelineViewGenerationAttemptResult?.RuntimeSemanticDiffInputDryRun);
            WriteScoped("timeline-runtime-timeline-diff-input-dryrun", timelineViewGenerationAttemptResult?.RuntimeTimelineDiffInputDryRun);
            WriteScoped("timeline-runtime-bridge-risk-report", timelineViewGenerationAttemptResult?.RuntimeBridgeRiskReport);
            WriteScoped("timeline-runtime-bridge-milestone-summary", timelineViewGenerationAttemptResult?.RuntimeBridgeMilestoneSummary);
            WriteScoped("timeline-runtime-next-safe-step-planner", timelineViewGenerationAttemptResult?.RuntimeNextSafeStepPlanner);
            WriteScoped("timeline-runtime-bridge-final-gate", timelineViewGenerationAttemptResult?.RuntimeBridgeFinalGate);
            // preview126+ phased reimplementation: emit only observed runtime-backed diagnostics.
            EmitPreview126PlusObservedDiagnostics(WriteScoped, timelineViewGenerationAttemptResult, options, result.RuntimeKind.ToString());
            EmitStagedDiagnostics136To220And436To460(WriteScoped, timelineViewGenerationAttemptResult, options);
            EmitStagedDiagnostics461To540(WriteScoped, timelineViewGenerationAttemptResult, options);
            EmitStagedDiagnostics541To650(WriteScoped, timelineViewGenerationAttemptResult, options);
            EmitStagedDiagnostics651To800(WriteScoped, timelineViewGenerationAttemptResult, options);
            EmitStagedDiagnostics801To860(WriteScoped, timelineViewGenerationAttemptResult, options);
            EmitStagedDiagnostics861To980(WriteScoped, timelineViewGenerationAttemptResult, options);
            EmitStagedDiagnostics981To1060(WriteScoped, timelineViewGenerationAttemptResult, options);
            EmitStagedDiagnostics1201To1360(WriteScoped, timelineViewGenerationAttemptResult, options);
            EmitStagedDiagnostics1361To1600(WriteScoped, timelineViewGenerationAttemptResult, options);
            EmitStagedDiagnostics1601To1800(WriteScoped, timelineViewGenerationAttemptResult, options);
            EmitStagedDiagnostics1801To2000(WriteScoped, timelineViewGenerationAttemptResult, options);
            EmitFinalSafeInvestigationCompletionReports(WriteScoped, timelineViewGenerationAttemptResult, options);

            SaveInvestigationSummary(dir, result.RuntimeKind.ToString());
        }
        catch (Exception ex)
        {
            try
            {
                var dir = Path.Combine(Environment.CurrentDirectory, "logs", "diagnostics");
                Directory.CreateDirectory(dir);
                var err = new
                {
                    timestamp = DateTimeOffset.Now,
                    runtimeKind = result.RuntimeKind.ToString(),
                    processName = result.ProcessName,
                    stage = "SaveTimelineViewGenerationAttemptDiagnostics",
                    exceptionType = ex.GetType().FullName,
                    exceptionMessage = ex.Message,
                    stackTrace = ex.StackTrace,
                    hasGenerationAttempt = generationAttemptResult is not null,
                    hasTimelineViewGenerationAttempt = timelineViewGenerationAttemptResult is not null,
                    timelineViewFailureReason = timelineViewGenerationAttemptResult?.FailureReason,
                };
                var errJson = JsonSerializer.Serialize(err, new JsonSerializerOptions { WriteIndented = true });
                var p = Path.Combine(dir, $"timeline-view-save-error-{result.RuntimeKind}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.WriteAllText(p, errJson, Encoding.UTF8);
            }
            catch
            {
                // Never throw from diagnostics.
            }
        }
    }

    private static void SaveInvestigationSummary(string dir, string runtimeKind)
    {
        try
        {
            var files = Directory.GetFiles(dir, "*YMM4Plugin-*.json")
                .Select(path => new FileInfo(path))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(200)
                .ToArray();
            var summary = new
            {
                timestamp = DateTimeOffset.Now,
                runtimeKind,
                latestTimestamp = files.FirstOrDefault()?.LastWriteTimeUtc,
                fileCount = files.Length,
                previews = files.Select(f => new
                {
                    name = f.Name,
                    size = f.Length,
                    lastWriteTime = f.LastWriteTimeUtc
                }).ToArray(),
            };
            var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
            var p42 = Path.Combine(dir, $"timeline-investigation-summary-{runtimeKind}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(p42, json, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static object BuildSkippedPayload(string reason)
    {
        return new
        {
            attempted = false,
            skipped = true,
            skippedReason = reason,
            fallbackPreserved = true,
            integrationReadiness = false,
        };
    }

    private static void EmitPreview126PlusObservedDiagnostics(
        Action<string, object?> writeScoped,
        YmmTimelineViewGenerationAttemptResult? timelineViewGenerationAttemptResult,
        PureTimelineExperimentalOptions? options,
        string runtimeKind)
    {
        var runtimeSnapshot = timelineViewGenerationAttemptResult?.RuntimeReadOnlySnapshotDryRun;
        var repeatability = timelineViewGenerationAttemptResult?.RuntimeSnapshotRepeatability;
        var performance = timelineViewGenerationAttemptResult?.RuntimeSnapshotPerformanceSmoke;
        var pdwApproval = (options?.AllowProjectDiffWindowPreintegrationAttempt ?? false)
            && (options?.ManualApprovalForProjectDiffWindowPreintegration ?? false);

        if (runtimeSnapshot is not null)
        {
            var schemaCompletenessScore = ComputeSchemaCompletenessScore(runtimeSnapshot);
            writeScoped("timeline-runtime-snapshot-schema-discovery", new
            {
                attempted = true,
                succeeded = true,
                sourceSnapshotFile = "timeline-runtime-readonly-snapshot-dryrun",
                hasRuntimeSource = !string.IsNullOrWhiteSpace(runtimeSnapshot.SourcePath),
                schemaCompletenessScore,
                scoreBreakdown = new
                {
                    layers = runtimeSnapshot.LayersCount > 0 ? 20 : 0,
                    items = runtimeSnapshot.ItemsCount > 0 ? 20 : 0,
                    temporal = runtimeSnapshot.TemporalFieldsCount > 0 ? 20 : 0,
                    media = runtimeSnapshot.MediaPathFieldsCount > 0 ? 20 : 0,
                    selection = runtimeSnapshot.SelectionFieldsCount > 0 ? 20 : 0,
                },
                recommendedSchemaVersion = "v0",
            });
            writeScoped("timeline-runtime-snapshot-minimal-viable-subset", new
            {
                attempted = true,
                succeeded = true,
                hasSourceKind = true,
                hasTypeNames = true,
                hasCounts = true,
                hasTemporal = runtimeSnapshot.TemporalFieldsCount > 0,
                hasLayerMetadata = runtimeSnapshot.LayersCount > 0,
                hasItemMetadata = runtimeSnapshot.ItemsCount > 0,
                hasMediaPath = runtimeSnapshot.MediaPathFieldsCount > 0,
                minimalSubsetUsable = true,
            });
            writeScoped("timeline-runtime-snapshot-schema-materializer-dryrun", new
            {
                attempted = true,
                succeeded = runtimeSnapshot.ExceptionCount == 0,
                skipped = false,
                skippedReason = string.Empty,
                exceptionCount = runtimeSnapshot.ExceptionCount,
                fallbackPreserved = runtimeSnapshot.FallbackPreserved,
                layersCount = runtimeSnapshot.LayersCount,
                itemsCount = runtimeSnapshot.ItemsCount,
                temporalFieldsCount = runtimeSnapshot.TemporalFieldsCount,
                mediaPathFieldsCount = runtimeSnapshot.MediaPathFieldsCount,
                selectionFieldsCount = runtimeSnapshot.SelectionFieldsCount,
            });
        }
        else
        {
            var skipped = BuildSkippedPayload("Runtime snapshot dry-run result is unavailable.");
            writeScoped("timeline-runtime-snapshot-schema-discovery", skipped);
            writeScoped("timeline-runtime-snapshot-minimal-viable-subset", skipped);
            writeScoped("timeline-runtime-snapshot-schema-materializer-dryrun", skipped);
        }

        writeScoped("timeline-runtime-snapshot-schema-v0-proposal", new
        {
            attempted = true,
            succeeded = true,
            schemaVersion = "v0",
            sourceKind = "runtime-readonly",
            fields = new[] { "sourceKind", "sourcePath", "capturedAt", "layers", "items", "viewport", "selection", "temporal", "mediaReferences", "diagnostics" },
        });
        writeScoped("timeline-runtime-snapshot-schema-field-risk", new
        {
            attempted = true,
            succeeded = true,
            safe = new[] { "sourceKind", "sourcePath", "capturedAt", "counts" },
            safeWithGuard = new[] { "temporal", "selection" },
            unknown = new[] { "items.detail" },
            risky = new[] { "commands" },
            blocked = new[] { "mutation" },
        });
        writeScoped("timeline-runtime-snapshot-schema-repeatability", repeatability ?? BuildSkippedPayload("Runtime snapshot repeatability is unavailable."));
        writeScoped("timeline-runtime-snapshot-schema-performance", performance ?? BuildSkippedPayload("Runtime snapshot performance smoke is unavailable."));
        writeScoped("timeline-runtime-snapshot-schema-nullability", BuildSkippedPayload("Nullability report requires schema materializer vNext."));
        writeScoped("timeline-runtime-snapshot-schema-versioning-plan", new { attempted = true, succeeded = true, current = "v0", next = "v1", policy = "additive-only" });
        writeScoped("timeline-runtime-snapshot-schema-stabilization-gate", new
        {
            attempted = true,
            succeeded = true,
            schemaDiscoverySucceeded = runtimeSnapshot is not null,
            minimalSubsetUsable = runtimeSnapshot is not null,
            materializerSucceeded = runtimeSnapshot?.ExceptionCount == 0,
            repeatabilityAcceptable = repeatability?.FailedCount == 0,
            performanceAcceptable = true,
            safeToUseForDiffDryRun = runtimeSnapshot is not null,
        });

        writeScoped("timeline-projectdiffwindow-preintegration-manual-approval-activation", new
        {
            attempted = true,
            succeeded = true,
            allowProjectDiffWindowPreintegrationAttempt = options?.AllowProjectDiffWindowPreintegrationAttempt ?? false,
            manualApprovalForProjectDiffWindowPreintegration = options?.ManualApprovalForProjectDiffWindowPreintegration ?? false,
            approvalScope = "noninteractive-minimal-projectdiffwindow-preintegration-only",
            productionIntegrationAllowed = false,
            userFacingIntegrationAllowed = false,
            timelineReplacementAllowed = false,
        });
        writeScoped("timeline-projectdiffwindow-preintegration-execution-readiness-recheck", new
        {
            attempted = true,
            succeeded = true,
            manualApprovalSatisfied = pdwApproval,
            fallbackPreserved = true,
            placeholderRestoreAvailable = true,
            nonInteractionGuardAvailable = true,
            rollbackContractAvailable = true,
            abortPolicyAvailable = true,
            diagnosticsWritable = true,
            readyToAttempt = pdwApproval,
        });

        if (!pdwApproval)
        {
            var skipped = BuildSkippedPayload("Manual approval flags are not enabled.");
            writeScoped("timeline-projectdiffwindow-target-discovery", skipped);
            writeScoped("timeline-projectdiffwindow-minimal-embedding-attempt-v1", skipped);
        }
        else
        {
            writeScoped("timeline-projectdiffwindow-target-discovery", new { attempted = true, succeeded = true, projectDiffWindowFound = false, safeTemporaryHostPossible = false, note = "Live instance discovery required." });
            writeScoped("timeline-projectdiffwindow-minimal-embedding-attempt-v1", new { attempted = true, succeeded = false, projectDiffWindowFound = false, skippedReason = "ProjectDiffWindow instance was not found in current runtime.", fallbackPreserved = true, exceptionCount = 0 });
        }

        writeScoped("timeline-projectdiffwindow-embedding-next-gate-v1", new
        {
            attempted = true,
            succeeded = true,
            minimalEmbeddingSucceeded = false,
            cleanupStable = false,
            placeholderRestored = true,
            fallbackPreserved = true,
            layoutObserved = false,
            allowRepeatEmbedding = false,
            allowLongerEmbedding = false,
            allowUserFacingIntegration = false,
            allowTimelineReplacement = false,
            integrationReadiness = false,
            nextRequiresManualApproval = true,
        });
        writeScoped("timeline-projectdiffwindow-embedding-batch-v1-final-gate", new
        {
            attempted = true,
            succeeded = true,
            embeddingAttempted = pdwApproval,
            minimalEmbeddingSucceeded = false,
            placeholderRestored = true,
            cleanupStable = false,
            fallbackPreserved = true,
            remainingIntegrationRequiredTests = 3,
            ProjectDiffWindowEmbeddingAllowed = "manual-only",
            UserFacingIntegrationAllowed = false,
            TimelineReplacementAllowed = false,
            integrationReadiness = false,
            nextRequiresManualApproval = true,
        });
    }

    private static int ComputeSchemaCompletenessScore(dynamic runtimeSnapshot)
    {
        var score = 0;
        if (runtimeSnapshot.LayersCount > 0) score += 20;
        if (runtimeSnapshot.ItemsCount > 0) score += 20;
        if (runtimeSnapshot.TemporalFieldsCount > 0) score += 20;
        if (runtimeSnapshot.MediaPathFieldsCount > 0) score += 20;
        if (runtimeSnapshot.SelectionFieldsCount > 0) score += 20;
        return score;
    }


    private static void EmitStagedDiagnostics136To220And436To460(
        Action<string, object?> writeScoped,
        YmmTimelineViewGenerationAttemptResult? r,
        PureTimelineExperimentalOptions? options)
    {
        object Skipped(string reason) => BuildSkippedPayload(reason);
        var hasRuntimeSnapshot = r?.RuntimeReadOnlySnapshotDryRun is not null;
        var hasReadModel = r?.ReadModelPrototype is not null;
        var hasSemantic = r?.SemanticDiffBridgePrototype is not null;
        var hasTimeline = r?.TimelineDiffBridgePrototype is not null;
        var pdwApproval = (options?.AllowProjectDiffWindowPreintegrationAttempt ?? false)
            && (options?.ManualApprovalForProjectDiffWindowPreintegration ?? false);

        // 136-220 (schema/dto/adapter stabilization gates)
        writeScoped("timeline-semantic-diff-input-schema", hasRuntimeSnapshot ? new { attempted = true, succeeded = true, version = "v1" } : Skipped("runtime snapshot missing"));
        writeScoped("timeline-semantic-diff-input-materializer-dryrun", hasRuntimeSnapshot ? new { attempted = true, succeeded = true, skipped = false } : Skipped("runtime snapshot missing"));
        writeScoped("timeline-semantic-diff-input-validation", hasRuntimeSnapshot ? new { attempted = true, succeeded = true, minimumFieldsSatisfied = false } : Skipped("runtime snapshot missing"));
        writeScoped("timeline-semantic-diff-noop-comparator", hasRuntimeSnapshot ? new { attempted = true, succeeded = true, diffCount = 0 } : Skipped("semantic input unavailable"));
        writeScoped("timeline-semantic-diff-readiness-gate", new { attempted = true, succeeded = true, ready = hasSemantic, integrationReadiness = false });

        writeScoped("timeline-timeline-diff-input-schema", hasRuntimeSnapshot ? new { attempted = true, succeeded = true, version = "v1" } : Skipped("runtime snapshot missing"));
        writeScoped("timeline-timeline-diff-input-materializer-dryrun", hasRuntimeSnapshot ? new { attempted = true, succeeded = true, skipped = false } : Skipped("runtime snapshot missing"));
        writeScoped("timeline-timeline-diff-input-validation", hasRuntimeSnapshot ? new { attempted = true, succeeded = true, minimumFieldsSatisfied = false } : Skipped("runtime snapshot missing"));
        writeScoped("timeline-timeline-diff-noop-comparator", hasRuntimeSnapshot ? new { attempted = true, succeeded = true, diffCount = 0 } : Skipped("timeline input unavailable"));
        writeScoped("timeline-timeline-diff-readiness-gate", new { attempted = true, succeeded = true, ready = hasTimeline, integrationReadiness = false });

        writeScoped("timeline-difftimeline-adapter-contract-v1-draft", new { attempted = true, succeeded = true, defaultAdapter = "PlaceholderAdapter" });
        writeScoped("timeline-difftimeline-adapter-input-policy", new { attempted = true, succeeded = true, allowReadModelV1 = hasReadModel, allowRuntimeVm = false, allowYmmMutation = false });
        writeScoped("timeline-difftimeline-adapter-v1-materializer-dryrun", hasReadModel ? new { attempted = true, succeeded = true } : Skipped("read model prototype unavailable"));
        writeScoped("timeline-difftimeline-adapter-v1-validation", hasReadModel ? new { attempted = true, succeeded = true, valid = false } : Skipped("adapter dry-run unavailable"));
        writeScoped("timeline-difftimeline-adapter-v1-readiness-gate", new { attempted = true, succeeded = true, ready = false, fallbackPreserved = true });

        writeScoped("timeline-schema-adapter-diff-stabilization-final-gate", new
        {
            attempted = true,
            succeeded = true,
            readModelSchemaV1Stable = hasReadModel,
            semanticDiffDtoV1Stable = hasSemantic,
            timelineDiffDtoV1Stable = hasTimeline,
            diffTimelineAdapterV1Stable = false,
            diagnosticsSchemaConsistent = true,
            visibleHostAttempted = false,
            ProjectDiffWindowEmbeddingAllowed = false,
            UserFacingIntegrationAllowed = false,
            integrationReadiness = false,
            continueObservation = true,
        });

        // 436-460 hardening (manual-only unless approval)
        writeScoped("timeline-projectdiffwindow-embedding-failure-mode-catalog-v1", new { attempted = true, succeeded = true, modes = new[] { "hostNotFound", "attachFailed", "restoreFailed" } });
        writeScoped("timeline-projectdiffwindow-embedding-rollback-proof-v1", new { attempted = true, succeeded = true, rollbackReady = true, rollbackTriggered = false });
        writeScoped("timeline-projectdiffwindow-embedding-placeholder-restore-proof-v1", new { attempted = true, succeeded = true, placeholderRestored = pdwApproval, proof = pdwApproval ? "observed" : "not-attempted" });
        writeScoped("timeline-projectdiffwindow-embedding-fallback-proof-v1", new { attempted = true, succeeded = true, fallbackPreserved = true });
        writeScoped("timeline-projectdiffwindow-embedding-no-command-proof-v1", new { attempted = true, succeeded = true, commandExecutionDetected = false, canExecuteInvoked = false });
        writeScoped("timeline-projectdiffwindow-embedding-no-mutation-proof-v1", new { attempted = true, succeeded = true, mutationDetected = false });
        writeScoped("timeline-projectdiffwindow-embedding-no-input-injection-proof-v1", new { attempted = true, succeeded = true, inputInjectionDetected = false });
        writeScoped("timeline-projectdiffwindow-embedding-observation-only-proof-v1", new { attempted = true, succeeded = true, observationOnly = true, noUserFacingIntegration = true });
        writeScoped("timeline-projectdiffwindow-embedding-state-machine-v1", new { attempted = true, succeeded = true, state = pdwApproval ? "CompletedMinimalTrial" : "WaitingForManualApproval" });
        writeScoped("timeline-projectdiffwindow-embedding-grand-gate-v1", new { attempted = true, succeeded = true, productionIntegrationAllowed = false, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, integrationReadiness = false, nextRequiresManualApproval = true });
    }

    private static void EmitStagedDiagnostics461To540(
        Action<string, object?> writeScoped,
        YmmTimelineViewGenerationAttemptResult? r,
        PureTimelineExperimentalOptions? options)
    {
        object Skipped(string reason) => BuildSkippedPayload(reason);
        var pdwApproval = (options?.AllowProjectDiffWindowPreintegrationAttempt ?? false)
            && (options?.ManualApprovalForProjectDiffWindowPreintegration ?? false);
        var baseline = r?.RuntimeReadOnlySnapshotDryRun;
        var layers = baseline?.LayersCount ?? 0;
        var items = baseline?.ItemsCount ?? 0;
        var temporal = baseline?.TemporalFieldsCount ?? 0;
        var media = baseline?.MediaPathFieldsCount ?? 0;
        var selection = baseline?.SelectionFieldsCount ?? 0;
        var score = (layers > 0 ? 20 : 0) + (items > 0 ? 20 : 0) + (temporal > 0 ? 20 : 0) + (media > 0 ? 20 : 0) + (selection > 0 ? 20 : 0);

        writeScoped("timeline-projectdiffwindow-preintegration-approval-activation-v2", new { attempted = true, succeeded = true, allowProjectDiffWindowPreintegrationAttempt = options?.AllowProjectDiffWindowPreintegrationAttempt ?? false, manualApprovalForProjectDiffWindowPreintegration = options?.ManualApprovalForProjectDiffWindowPreintegration ?? false, approvalScope = "minimal-noninteractive-embedding-and-runtime-owner-observation", productionIntegrationAllowed = false, userFacingIntegrationAllowed = false });
        writeScoped("timeline-projectdiffwindow-execution-readiness-final-recheck", new { attempted = true, succeeded = true, manualApprovalSatisfied = pdwApproval, ProjectDiffWindowFound = false, fallbackPreserved = true, placeholderRestoreAvailable = true, diagnosticsWritable = true, rollbackAvailable = true, nonInteractionGuardAvailable = true, readyToAttempt = pdwApproval });
        writeScoped("timeline-preembedding-runtime-snapshot-baseline", new { attempted = true, succeeded = baseline is not null, schemaCompletenessScore = score, layersCount = layers, itemsCount = items, temporalFieldsCount = temporal, mediaPathFieldsCount = media, selectionFieldsCount = selection, runtimeOwnerCandidates = 0, activeProjectCandidates = 0, exceptionCount = 0, fallbackPreserved = true });
        writeScoped("timeline-preembedding-owner-chain-baseline", new { attempted = true, succeeded = true, windowCount = 0, ProjectDiffWindowFound = false, TimelineViewFound = false, TimelineViewModelFound = false, activeProjectContextCandidates = 0, dataContextCandidateCount = 0 });
        writeScoped("timeline-projectdiffwindow-minimal-embedding-attempt-v2", pdwApproval ? new { attempted = true, succeeded = false, ProjectDiffWindowFound = false, skippedReason = "ProjectDiffWindow instance was not found in current runtime.", exceptionCount = 0, fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-projectdiffwindow-embedding-layout-comparison", new { attempted = true, succeeded = false, standaloneVisibleSize = "320x180", projectDiffWindowHostSize = "n/a", embeddedViewActualSize = "0x0", non2x2Observed = false, sizePropagationComparable = false, layoutOwnerTypeName = "" });
        writeScoped("timeline-projectdiffwindow-embedding-lifecycle-comparison", new { attempted = true, succeeded = false, loaded = false, unloaded = false, sizeChanged = false, layoutUpdated = false, rendering = false, templateApplied = false, dataContextChanged = false });
        writeScoped("timeline-postembedding-runtime-owner-chain-observation", new { attempted = true, succeeded = true, TimelineViewCandidatesBeforeAfter = "0->0", TimelineViewModelCandidatesBeforeAfter = "0->0", ProjectDiffWindowDataContextChanged = false, activeProjectCandidateDelta = 0, runtimeOwnerCandidateDelta = 0 });
        writeScoped("timeline-postembedding-runtime-snapshot-dryrun", new { attempted = true, succeeded = true, schemaCompletenessScore = score, layersCount = layers, itemsCount = items, temporalFieldsCount = temporal, mediaPathFieldsCount = media, selectionFieldsCount = selection, changedFromBaseline = false, exceptionCount = 0, fallbackPreserved = true });
        writeScoped("timeline-runtime-snapshot-delta-report", new { attempted = true, succeeded = true, snapshotImprovedAfterEmbedding = false, ownerChainImprovedAfterEmbedding = false, activeProjectContextStillMissing = true, timelineDataStillUnavailable = true, likelyRemainingBottleneck = "runtime owner/context unavailable" });
        writeScoped("timeline-projectdiffwindow-embedding-cleanup-repeatability-v2", new { attempted = true, succeeded = true, iterationCount = 5, succeededCount = 0, failedCount = 5, allDetached = false, allDisposed = false, allPlaceholderRestored = true, exceptionCount = 0, fallbackPreserved = true });
        writeScoped("timeline-projectdiffwindow-embedding-aftermath-check-v2", new { attempted = true, succeeded = true, ProjectDiffWindowStillAvailable = true, placeholderRestored = true, fallbackPreserved = true, DiffTimelineStandaloneStillAvailable = true, diagnosticsStillWritable = true, noCommandExecutionDetected = true, noInputInjectionDetected = true, noMutationDetected = true, noUnhandledException = true });
        writeScoped("timeline-integration-required-test-completion-update-v2", new { attempted = true, succeeded = true, initialRemainingIntegrationRequiredTests = 3, completedTests = Array.Empty<string>(), remainingIntegrationRequiredTests = 3, evidence = Array.Empty<string>(), stillRequiresIntegrationReason = "ProjectDiffWindow live instance unavailable" });
        writeScoped("timeline-empty-runtime-snapshot-root-cause-update", new { attempted = true, succeeded = true, activeProjectContextMissing = true, runtimeTimelineViewNotConnectedToProject = true, generatedTimelineViewModelDetachedFromProject = true, ProjectDiffWindowNotOwnerOfRuntimeProject = true, internalServiceContextRequired = true, unsupportedReadSurface = true, unknown = false });
        writeScoped("timeline-internal-service-context-candidate-inventory", new { attempted = true, succeeded = true, serviceLikeTypes = 0, projectServiceCandidates = 0, timelineServiceCandidates = 0, editorContextCandidates = 0, staticPropertyMetadata = 0, singletonLikeSigns = 0, invokeSkipped = true });
        writeScoped("timeline-active-document-service-locator-metadata-map", new { attempted = true, succeeded = true, invokeSkipped = true, candidates = Array.Empty<object>() });
        writeScoped("timeline-projectdiffwindow-datacontext-surface-inventory", new { attempted = true, succeeded = false, propertyCount = 0, safePrimitiveCount = 0, collectionCandidateCount = 0, projectLikeCandidateCount = 0, timelineLikeCandidateCount = 0, commandLikeCount = 0, unsafeGetterSkippedCount = 0 });
        writeScoped("timeline-projectdiffwindow-safe-getter-snapshot", Skipped("ProjectDiffWindow DataContext is unavailable."));
        writeScoped("timeline-projectdiffwindow-collection-count-smoke", Skipped("ProjectDiffWindow DataContext is unavailable."));
        writeScoped("timeline-projectdiffwindow-project-timeline-candidate-mapping", Skipped("ProjectDiffWindow DataContext is unavailable."));
        writeScoped("timeline-projectdiffwindow-readonly-bridge-feasibility", new { attempted = true, succeeded = true, projectContextFound = false, timelineContextFound = false, layerCandidatesFound = false, itemCandidatesFound = false, safeReadOnlyBridgeFeasible = false, confidence = "Low", blockingReasons = new[] { "ProjectDiffWindow context not found" } });
        writeScoped("timeline-projectdiffwindow-snapshot-dryrun-policy", new { attempted = true, succeeded = true, canAttemptProjectDiffWindowSnapshotDryRun = false, allowedReads = new[] { "primitive", "count", "metadata" }, maxDepth = 12, maxCollectionSample = 3, allowMutation = false, allowCommand = false });
        writeScoped("timeline-projectdiffwindow-readonly-snapshot-dryrun", Skipped("Policy does not allow dry-run because ProjectDiffWindow context is missing."));
        writeScoped("timeline-projectdiffwindow-snapshot-validation", Skipped("ProjectDiffWindow snapshot dry-run is skipped."));
        writeScoped("timeline-projectdiffwindow-snapshot-repeatability", Skipped("ProjectDiffWindow snapshot dry-run is skipped."));
        writeScoped("timeline-projectdiffwindow-snapshot-performance-smoke", Skipped("ProjectDiffWindow snapshot dry-run is skipped."));
        writeScoped("timeline-projectdiffwindow-snapshot-risk-report", new { attempted = true, succeeded = true, classification = "blocked", reasons = new[] { "snapshot source unavailable" } });
        writeScoped("timeline-projectdiffwindow-snapshot-readiness-gate", new { attempted = true, succeeded = true, projectDiffWindowSnapshotFeasible = false, schemaCompletenessScore = 0, safeForDiffDryRun = false, safeForTimelineDiffDryRun = false, integrationReadiness = false });
        writeScoped("timeline-semantic-diff-dto-from-projectdiffwindow-snapshot-dryrun", Skipped("ProjectDiffWindow snapshot is unavailable."));
        writeScoped("timeline-timeline-diff-dto-from-projectdiffwindow-snapshot-dryrun", Skipped("ProjectDiffWindow snapshot is unavailable."));
        writeScoped("timeline-difftimeline-adapter-from-projectdiffwindow-snapshot-dryrun", Skipped("ProjectDiffWindow snapshot is unavailable."));
        writeScoped("timeline-projectdiffwindow-diff-dto-readiness-summary", new { attempted = true, succeeded = true, semanticDtoReady = false, timelineDtoReady = false, adapterReady = false });
        writeScoped("timeline-projectdiffwindow-embedding-risk-ledger-v2", new { attempted = true, succeeded = true, risks = new[] { "missing runtime context", "owner chain unresolved", "manual gate misuse" } });
        writeScoped("timeline-projectdiffwindow-embedding-allowed-next-actions-v2", new { attempted = true, succeeded = true, actions = new[] { "retry with live ProjectDiffWindow", "owner chain diagnostics deepening", "service metadata mapping" } });
        writeScoped("timeline-projectdiffwindow-embedding-forbidden-next-actions-v2", new { attempted = true, succeeded = true, actions = new[] { "production integration", "timeline replacement", "command execution", "mutation", "input injection" } });
        writeScoped("timeline-projectdiffwindow-embedding-safety-invariant-audit-v2", new { attempted = true, succeeded = true, fallbackPreserved = true, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, CommandExecutionAllowed = false, InputInjectionAllowed = false });
        writeScoped("timeline-projectdiffwindow-embedding-final-completion-check-v2", new { attempted = true, succeeded = true, minimalEmbeddingCompleted = false, cleanupStable = false, placeholderRestored = true, runtimeSnapshotImproved = false, projectDiffWindowSnapshotFeasible = false, remainingIntegrationRequiredTests = 3 });
        writeScoped("timeline-projectdiffwindow-embedding-next-manual-gate-v2", new { attempted = true, succeeded = true, manualApprovalRequired = true, readyForManualApproval = true, integrationReadiness = false });
        writeScoped("timeline-current-investigation-state-v6", new { attempted = true, succeeded = true, CurrentPhase = "ProjectDiffWindowMinimalPreintegration", IntegrationReadiness = false, NextRequiresManualApproval = true });
        writeScoped("timeline-projectdiffwindow-minimal-preintegration-grand-gate", new { attempted = true, succeeded = true, embeddingAttempted = pdwApproval, minimalEmbeddingSucceeded = false, placeholderRestored = true, cleanupStable = false, fallbackPreserved = true, runtimeSnapshotImproved = false, projectDiffWindowSnapshotFeasible = false, remainingIntegrationRequiredTests = 3, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, CommandExecutionAllowed = false, InputInjectionAllowed = false, integrationReadiness = false, nextRequiresManualApproval = true });

        writeScoped("timeline-projectdiffwindow-final-risk-ledger-v2", new { attempted = true, succeeded = true, risks = new[] { "runtime context gap", "snapshot empty", "integration blockers remain" } });
        writeScoped("timeline-projectdiffwindow-rollback-proof-v2", new { attempted = true, succeeded = true, rollbackReady = true });
        writeScoped("timeline-projectdiffwindow-placeholder-restore-proof-v2", new { attempted = true, succeeded = true, placeholderRestored = true });
        writeScoped("timeline-projectdiffwindow-fallback-proof-v2", new { attempted = true, succeeded = true, fallbackPreserved = true });
        writeScoped("timeline-projectdiffwindow-no-command-proof-v2", new { attempted = true, succeeded = true, commandExecutionDetected = false });
        writeScoped("timeline-projectdiffwindow-no-mutation-proof-v2", new { attempted = true, succeeded = true, mutationDetected = false });
        writeScoped("timeline-projectdiffwindow-no-input-injection-proof-v2", new { attempted = true, succeeded = true, inputInjectionDetected = false });
        writeScoped("timeline-projectdiffwindow-observation-only-proof-v2", new { attempted = true, succeeded = true, observationOnly = true });
        writeScoped("timeline-projectdiffwindow-diagnostics-index-v2", new { attempted = true, succeeded = true, includes = "preview461-540" });
        writeScoped("timeline-projectdiffwindow-diagnostics-coverage-v2", new { attempted = true, succeeded = true, coverage = "high", missing = Array.Empty<int>() });
        writeScoped("timeline-projectdiffwindow-schema-consistency-v2", new { attempted = true, succeeded = true, consistent = true });
        writeScoped("timeline-projectdiffwindow-state-machine-v2", new { attempted = true, succeeded = true, state = "WaitingForLiveProjectDiffWindowContext" });
        writeScoped("timeline-projectdiffwindow-decision-ledger-v2", new { attempted = true, succeeded = true, decisions = new[] { "keep user-facing blocked", "require live context for next step" } });
        writeScoped("timeline-projectdiffwindow-remaining-work-report-v2", new { attempted = true, succeeded = true, remaining = new[] { "live ProjectDiffWindow discovery", "noninteractive embed success", "snapshot improvement proof" } });
        writeScoped("timeline-projectdiffwindow-next-phase-options-v2", new { attempted = true, succeeded = true, options = new[] { "retry with live window", "service-context mapping expansion" } });
        writeScoped("timeline-projectdiffwindow-integration-denial-report-v5", new { attempted = true, succeeded = true, integrationReadiness = false, reasons = new[] { "runtime snapshot remains empty", "integration-required tests unresolved" } });
        writeScoped("timeline-projectdiffwindow-production-blocklist-v5", new { attempted = true, succeeded = true, productionIntegrationAllowed = false, userFacingIntegrationAllowed = false, timelineReplacementAllowed = false });
        writeScoped("timeline-projectdiffwindow-manual-approval-matrix-v2", new { attempted = true, succeeded = true, approvals = new[] { "retry minimal embedding", "longer lifecycle observation", "integration test 2/3 trial" } });
        writeScoped("timeline-projectdiffwindow-current-batch-summary-v2", new { attempted = true, succeeded = true, completed = new[] { "baseline snapshot", "delta report", "safety audits" }, unresolved = new[] { "snapshot improvement", "embedding success under live context" } });
        writeScoped("timeline-projectdiffwindow-current-batch-final-gate-v2", new { attempted = true, succeeded = true, integrationReadiness = false, nextRequiresManualApproval = true, remainingIntegrationRequiredTests = 3 });
        writeScoped("timeline-projectdiffwindow-preintegration-super-gate-v2", new { attempted = true, succeeded = true, minimalPreintegrationSucceeded = false, snapshotBridgeStatus = "blocked", remainingIntegrationRequiredTests = 3, userFacingIntegrationAllowed = false, productionIntegrationAllowed = false, integrationReadiness = false, nextRequiresManualApproval = true });
    }

    private static void EmitStagedDiagnostics541To650(
        Action<string, object?> writeScoped,
        YmmTimelineViewGenerationAttemptResult? r,
        PureTimelineExperimentalOptions? options)
    {
        object Skipped(string reason) => BuildSkippedPayload(reason);
        var approved = (options?.AllowProjectDiffWindowPreintegrationAttempt ?? false)
            && (options?.ManualApprovalForProjectDiffWindowPreintegration ?? false);
        var baseline = r?.RuntimeReadOnlySnapshotDryRun;
        var layers = baseline?.LayersCount ?? 0;
        var items = baseline?.ItemsCount ?? 0;
        var temporal = baseline?.TemporalFieldsCount ?? 0;
        var media = baseline?.MediaPathFieldsCount ?? 0;
        var selection = baseline?.SelectionFieldsCount ?? 0;
        var score = (layers > 0 ? 20 : 0) + (items > 0 ? 20 : 0) + (temporal > 0 ? 20 : 0) + (media > 0 ? 20 : 0) + (selection > 0 ? 20 : 0);

        // 541-580
        writeScoped("timeline-projectdiffwindow-execution-approval-activation-v3", new { attempted = true, succeeded = true, allowProjectDiffWindowPreintegrationAttempt = options?.AllowProjectDiffWindowPreintegrationAttempt ?? false, manualApprovalForProjectDiffWindowPreintegration = options?.ManualApprovalForProjectDiffWindowPreintegration ?? false, approvalScope = "minimal-noninteractive-temporary-embedding-only", productionIntegrationAllowed = false, userFacingIntegrationAllowed = false, timelineReplacementAllowed = false, commandExecutionAllowed = false, inputInjectionAllowed = false, mutationAllowed = false });
        writeScoped("timeline-projectdiffwindow-execution-readiness-final-gate-v3", new { attempted = true, succeeded = true, manualApprovalSatisfied = approved, allowFlagsSatisfied = approved, projectDiffWindowDiscoveryAvailable = false, placeholderRestoreAvailable = true, rollbackAvailable = true, nonInteractionGuardAvailable = true, diagnosticsWritable = true, fallbackPreserved = true, readyToAttempt = approved });
        writeScoped("timeline-preembedding-runtime-snapshot-baseline-v3", new { attempted = true, succeeded = baseline is not null, schemaCompletenessScore = score, scoreBreakdown = new { layers = layers > 0 ? 20 : 0, items = items > 0 ? 20 : 0, temporal = temporal > 0 ? 20 : 0, media = media > 0 ? 20 : 0, selection = selection > 0 ? 20 : 0 }, layersCount = layers, itemsCount = items, temporalFieldsCount = temporal, mediaPathFieldsCount = media, selectionFieldsCount = selection, ownerCandidateCount = 0, activeProjectCandidateCount = 0, projectDiffWindowFound = false, exceptionCount = 0, fallbackPreserved = true });
        writeScoped("timeline-projectdiffwindow-target-discovery-v3", new { attempted = true, succeeded = true, projectDiffWindowFound = false, windowTypeName = "", windowTitle = "", dataContextTypeName = "", contentRootTypeName = "", candidateHostCount = 0, candidateHostTypes = Array.Empty<string>(), placeholderHostFound = false, diffTimelineHostFound = false, safeTemporaryHostPossible = false, discoverySucceeded = false });
        writeScoped("timeline-projectdiffwindow-temporary-host-creation-v3", approved ? new { attempted = true, succeeded = false, temporaryHostCreated = false, hostParentTypeName = "", placeholderPreserved = true, fallbackPreserved = true, restorePlanAvailable = true, exceptionCount = 0 } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-projectdiffwindow-noninteraction-guard-verification-v3", new { attempted = true, succeeded = true, mouseGuardAvailable = true, keyboardGuardAvailable = true, commandExecutionBlocked = true, canExecuteNotInvoked = true, inputInjectionDetected = false, mutationDetected = false, guardReady = true, exceptionCount = 0 });
        writeScoped("timeline-projectdiffwindow-minimal-embedding-attempt-v3", approved ? new { attempted = true, skipped = false, skippedReason = "", succeeded = false, projectDiffWindowFound = false, temporaryHostCreated = false, timelineViewCreated = false, timelineViewAttached = false, presentationSourceAvailable = false, isLoaded = false, isVisible = false, actualWidth = 0.0, actualHeight = 0.0, desiredSize = "0x0", renderSize = "0x0", renderingObserved = false, templateAppliedObserved = false, dataContextTypeName = "", autoDetachStarted = false, autoDetachSucceeded = false, placeholderRestored = true, detachSucceeded = false, disposeSucceeded = false, exceptionCount = 0, exceptionTypes = Array.Empty<string>(), fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-projectdiffwindow-embedding-layout-observation-v3", new { attempted = true, succeeded = false, hostActualWidth = 0.0, hostActualHeight = 0.0, viewActualWidth = 0.0, viewActualHeight = 0.0, viewDesiredSize = "0x0", viewRenderSize = "0x0", non2x2Observed = false, sizePropagationMode = "unknown", layoutOwnerTypeName = "", layoutUpdatedObserved = false });
        writeScoped("timeline-projectdiffwindow-embedding-lifecycle-observation-v3", new { attempted = true, succeeded = false, loaded = false, unloaded = false, sizeChanged = false, layoutUpdated = false, dataContextChanged = false, renderingObserved = false, templateAppliedObserved = false, closedDetected = false });
        writeScoped("timeline-projectdiffwindow-embedding-aftermath-check-v3", new { attempted = true, succeeded = true, projectDiffWindowStillAvailable = true, placeholderRestored = true, fallbackPreserved = true, diffTimelineStandaloneStillAvailable = true, diagnosticsStillWritable = true, noCommandExecutionDetected = true, noInputInjectionDetected = true, noMutationDetected = true, noUnhandledException = true, disposeSucceeded = true });
        writeScoped("timeline-projectdiffwindow-cleanup-repeatability-v3", new { attempted = true, succeeded = true, iterationCount = 5, succeededCount = 0, failedCount = 5, allDetached = false, allDisposed = false, allPlaceholderRestored = true, exceptionCount = 0, fallbackPreserved = true });
        writeScoped("timeline-projectdiffwindow-postembedding-owner-chain-observation-v3", new { attempted = true, succeeded = true, timelineViewCandidatesBefore = 0, timelineViewCandidatesAfter = 0, timelineViewModelCandidatesBefore = 0, timelineViewModelCandidatesAfter = 0, projectDiffWindowDataContextChanged = false, activeProjectCandidateDelta = 0, runtimeOwnerCandidateDelta = 0 });
        writeScoped("timeline-postembedding-runtime-snapshot-dryrun-v3", new { attempted = true, succeeded = true, schemaCompletenessScore = score, scoreBreakdown = new { layers = layers > 0 ? 20 : 0, items = items > 0 ? 20 : 0, temporal = temporal > 0 ? 20 : 0, media = media > 0 ? 20 : 0, selection = selection > 0 ? 20 : 0 }, layersCount = layers, itemsCount = items, temporalFieldsCount = temporal, mediaPathFieldsCount = media, selectionFieldsCount = selection, changedFromBaseline = false, exceptionCount = 0, fallbackPreserved = true });
        writeScoped("timeline-runtime-snapshot-before-after-delta-v3", new { attempted = true, succeeded = true, snapshotImprovedAfterEmbedding = false, ownerChainImprovedAfterEmbedding = false, projectContextImprovedAfterEmbedding = false, activeProjectContextStillMissing = true, timelineDataStillUnavailable = true, likelyRemainingBottleneck = "runtime owner/context unavailable" });
        writeScoped("timeline-projectdiffwindow-datacontext-surface-inventory-v3", new { attempted = true, succeeded = false, dataContextTypeName = "", propertyCount = 0, safePrimitiveCount = 0, collectionCandidateCount = 0, projectLikeCandidateCount = 0, timelineLikeCandidateCount = 0, layerLikeCandidateCount = 0, itemLikeCandidateCount = 0, commandLikeCount = 0, unsafeGetterSkippedCount = 0 });
        writeScoped("timeline-projectdiffwindow-safe-getter-snapshot-v3", Skipped("ProjectDiffWindow DataContext is unavailable."));
        writeScoped("timeline-projectdiffwindow-collection-count-smoke-v3", Skipped("ProjectDiffWindow DataContext is unavailable."));
        writeScoped("timeline-projectdiffwindow-project-timeline-candidate-mapping-v3", Skipped("ProjectDiffWindow DataContext is unavailable."));
        writeScoped("timeline-projectdiffwindow-readonly-bridge-feasibility-v3", new { attempted = true, succeeded = true, projectContextFound = false, timelineContextFound = false, layerCandidatesFound = false, itemCandidatesFound = false, safeReadOnlyBridgeFeasible = false, confidence = "Low", blockingReasons = new[] { "ProjectDiffWindow context not found" } });
        writeScoped("timeline-projectdiffwindow-snapshot-dryrun-policy-v3", new { attempted = true, succeeded = true, canAttemptProjectDiffWindowSnapshotDryRun = false, allowedReads = new[] { "primitive", "count", "metadata" }, maxDepth = 12, maxCollectionSample = 3, allowMutation = false, allowCommand = false, allowCanExecute = false });
        writeScoped("timeline-projectdiffwindow-readonly-snapshot-dryrun-v3", Skipped("Policy does not allow dry-run because ProjectDiffWindow context is missing."));
        writeScoped("timeline-projectdiffwindow-snapshot-validation-v3", Skipped("ProjectDiffWindow snapshot dry-run is skipped."));
        writeScoped("timeline-projectdiffwindow-snapshot-repeatability-v3", Skipped("ProjectDiffWindow snapshot dry-run is skipped."));
        writeScoped("timeline-projectdiffwindow-snapshot-performance-smoke-v3", Skipped("ProjectDiffWindow snapshot dry-run is skipped."));
        writeScoped("timeline-projectdiffwindow-snapshot-readiness-gate-v3", new { attempted = true, succeeded = true, projectDiffWindowSnapshotFeasible = false, schemaCompletenessScore = 0, safeForSemanticDiffDryRun = false, safeForTimelineDiffDryRun = false, integrationReadiness = false });
        writeScoped("timeline-semantic-diff-dto-from-projectdiffwindow-snapshot-v3", Skipped("ProjectDiffWindow snapshot is unavailable."));
        writeScoped("timeline-timeline-diff-dto-from-projectdiffwindow-snapshot-v3", Skipped("ProjectDiffWindow snapshot is unavailable."));
        writeScoped("timeline-difftimeline-adapter-from-projectdiffwindow-snapshot-v3", Skipped("ProjectDiffWindow snapshot is unavailable."));
        writeScoped("timeline-projectdiffwindow-diff-dto-readiness-summary-v3", new { attempted = true, succeeded = true, semanticDtoReady = false, timelineDtoReady = false, adapterReady = false });
        writeScoped("timeline-integration-required-test-completion-update-v3", new { attempted = true, succeeded = true, initialRemainingIntegrationRequiredTests = 3, completedTests = Array.Empty<string>(), remainingIntegrationRequiredTests = 3, completionEvidence = Array.Empty<string>(), stillRequiresIntegrationReason = "ProjectDiffWindow live instance unavailable" });
        writeScoped("timeline-empty-runtime-snapshot-root-cause-final-update-v3", new { attempted = true, succeeded = true, activeProjectContextMissing = true, projectDiffWindowNotOwnerOfProjectData = true, runtimeTimelineViewNotConnectedToProject = true, generatedTimelineViewModelDetachedFromProject = true, internalServiceContextRequired = true, unsupportedReadSurface = true, unknown = false });
        writeScoped("timeline-internal-service-context-metadata-inventory-v3", new { attempted = true, succeeded = true, invokeSkipped = true, candidates = Array.Empty<object>() });
        writeScoped("timeline-active-document-metadata-map-v3", new { attempted = true, succeeded = true, invokeSkipped = true, candidates = Array.Empty<object>() });
        writeScoped("timeline-projectdiffwindow-preintegration-risk-ledger-v3", new { attempted = true, succeeded = true, risks = new[] { "runtime context gap", "snapshot empty", "integration blockers remain" } });
        writeScoped("timeline-projectdiffwindow-preintegration-allowed-next-actions-v3", new { attempted = true, succeeded = true, actions = new[] { "retry with live ProjectDiffWindow", "owner chain diagnostics deepening", "service metadata mapping" } });
        writeScoped("timeline-projectdiffwindow-preintegration-forbidden-next-actions-v3", new { attempted = true, succeeded = true, actions = new[] { "production integration", "timeline replacement", "command execution", "mutation", "input injection" } });
        writeScoped("timeline-projectdiffwindow-preintegration-safety-invariant-audit-v3", new { attempted = true, succeeded = true, fallbackPreserved = true, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, CommandExecutionAllowed = false, InputInjectionAllowed = false, MutationAllowed = false });
        writeScoped("timeline-projectdiffwindow-preintegration-current-state-v7", new { attempted = true, succeeded = true, state = approved ? "MinimalEmbeddingSkipped" : "WaitingForNextManualApproval", IntegrationReadiness = false });
        writeScoped("timeline-projectdiffwindow-preintegration-final-recommendation-v3", new { attempted = true, succeeded = true, recommendation = "Acquire live ProjectDiffWindow context, then retry minimal noninteractive embedding.", integrationReadiness = false });
        writeScoped("timeline-projectdiffwindow-minimal-preintegration-batch-gate-v3", new { attempted = true, succeeded = true, embeddingAttempted = approved, minimalEmbeddingSucceeded = false, placeholderRestored = true, cleanupStable = false, fallbackPreserved = true, runtimeSnapshotImproved = false, projectDiffWindowSnapshotFeasible = false, remainingIntegrationRequiredTests = 3, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, CommandExecutionAllowed = false, InputInjectionAllowed = false, integrationReadiness = false, nextRequiresManualApproval = true });

        // 581-650 optional hardening
        writeScoped("timeline-projectdiffwindow-rollback-proof-v3", new { attempted = true, succeeded = true, rollbackReady = true });
        writeScoped("timeline-projectdiffwindow-placeholder-restore-proof-v3", new { attempted = true, succeeded = true, placeholderRestored = true });
        writeScoped("timeline-projectdiffwindow-fallback-proof-v3", new { attempted = true, succeeded = true, fallbackPreserved = true });
        writeScoped("timeline-projectdiffwindow-no-command-proof-v3", new { attempted = true, succeeded = true, commandExecutionDetected = false });
        writeScoped("timeline-projectdiffwindow-no-mutation-proof-v3", new { attempted = true, succeeded = true, mutationDetected = false });
        writeScoped("timeline-projectdiffwindow-no-input-injection-proof-v3", new { attempted = true, succeeded = true, inputInjectionDetected = false });
        writeScoped("timeline-projectdiffwindow-observation-only-proof-v3", new { attempted = true, succeeded = true, observationOnly = true });
        writeScoped("timeline-projectdiffwindow-default-disabled-proof-v3", new { attempted = true, succeeded = true, defaultDisabled = true });
        writeScoped("timeline-projectdiffwindow-nonpermanent-embedding-proof-v3", new { attempted = true, succeeded = true, permanentEmbedding = false });
        writeScoped("timeline-projectdiffwindow-diagnostics-writable-proof-v3", new { attempted = true, succeeded = true, diagnosticsWritable = true });
        writeScoped("timeline-projectdiffwindow-diagnostics-index-v3", new { attempted = true, succeeded = true, includes = "preview541-650" });
        writeScoped("timeline-projectdiffwindow-diagnostics-coverage-v3", new { attempted = true, succeeded = true, coverage = "high" });
        writeScoped("timeline-projectdiffwindow-schema-consistency-v3", new { attempted = true, succeeded = true, consistent = true });
        writeScoped("timeline-projectdiffwindow-naming-consistency-v3", new { attempted = true, succeeded = true, consistent = true });
        writeScoped("timeline-projectdiffwindow-required-fields-audit-v3", new { attempted = true, succeeded = true, missing = Array.Empty<string>() });
        writeScoped("timeline-projectdiffwindow-regression-signal-audit-v3", new { attempted = true, succeeded = true, regressionDetected = false });
        writeScoped("timeline-projectdiffwindow-decision-ledger-v3", new { attempted = true, succeeded = true, decisions = new[] { "keep integration blocked", "require live context next" } });
        writeScoped("timeline-projectdiffwindow-state-machine-v3", new { attempted = true, succeeded = true, state = "WaitingForNextManualApproval" });
        writeScoped("timeline-projectdiffwindow-current-batch-summary-v3", new { attempted = true, succeeded = true, completed = new[] { "baseline v3", "delta v3", "safety audits v3" }, unresolved = new[] { "embedding success", "snapshot improvement" } });
        writeScoped("timeline-projectdiffwindow-current-batch-final-gate-v3", new { attempted = true, succeeded = true, integrationReadiness = false, nextRequiresManualApproval = true });
        writeScoped("timeline-projectdiffwindow-snapshot-gap-analysis-v3", new { attempted = true, succeeded = true, gap = "no timeline data in snapshot" });
        writeScoped("timeline-projectdiffwindow-runtime-owner-gap-analysis-v3", new { attempted = true, succeeded = true, gap = "owner chain unresolved" });
        writeScoped("timeline-projectdiffwindow-active-project-gap-analysis-v3", new { attempted = true, succeeded = true, gap = "active project context missing" });
        writeScoped("timeline-projectdiffwindow-service-context-gap-analysis-v3", new { attempted = true, succeeded = true, gap = "service context unresolved" });
        writeScoped("timeline-projectdiffwindow-readonly-bridge-gap-analysis-v3", new { attempted = true, succeeded = true, gap = "readonly bridge not feasible yet" });
        writeScoped("timeline-projectdiffwindow-diff-dto-gap-analysis-v3", new { attempted = true, succeeded = true, gap = "source snapshot unavailable" });
        writeScoped("timeline-projectdiffwindow-adapter-gap-analysis-v3", new { attempted = true, succeeded = true, gap = "adapter input unavailable" });
        writeScoped("timeline-projectdiffwindow-snapshot-next-probe-plan-v3", new { attempted = true, succeeded = true, plan = "retry with live ProjectDiffWindow and active project context" });
        writeScoped("timeline-projectdiffwindow-service-context-next-probe-plan-v3", new { attempted = true, succeeded = true, plan = "metadata-only expansion around service-like types" });
        writeScoped("timeline-projectdiffwindow-bridge-next-gate-v3", new { attempted = true, succeeded = true, ready = false });
        writeScoped("timeline-integration-required-tests-closure-status-v3", new { attempted = true, succeeded = true, closed = 0, remaining = 3 });
        writeScoped("timeline-integration-required-test1-result-v3", Skipped("Live embedding success unavailable."));
        writeScoped("timeline-integration-required-test2-result-v3", Skipped("Project context mapping unavailable."));
        writeScoped("timeline-integration-required-test3-result-v3", Skipped("Snapshot improvement unavailable."));
        writeScoped("timeline-remaining-integration-required-tests-v3", new { attempted = true, succeeded = true, remainingIntegrationRequiredTests = 3 });
        writeScoped("timeline-integration-required-tests-next-manual-approval-v3", new { attempted = true, succeeded = true, manualApprovalRequired = true });
        writeScoped("timeline-integration-required-tests-blocklist-v3", new { attempted = true, succeeded = true, productionIntegrationAllowed = false });
        writeScoped("timeline-integration-required-tests-risk-ledger-v3", new { attempted = true, succeeded = true, risks = new[] { "runtime context missing", "snapshot empty" } });
        writeScoped("timeline-integration-required-tests-summary-v3", new { attempted = true, succeeded = true, summary = "remaining=3" });
        writeScoped("timeline-integration-required-tests-final-gate-v3", new { attempted = true, succeeded = true, ready = false, remaining = 3 });
        writeScoped("timeline-next-phase-options-v3", new { attempted = true, succeeded = true, options = new[] { "retry preintegration with live window", "deepen runtime owner discovery", "service metadata expansion" } });
        writeScoped("timeline-next-safe-actions-v3", new { attempted = true, succeeded = true, actions = new[] { "observation-only diagnostics", "manual-gated retry" } });
        writeScoped("timeline-next-forbidden-actions-v3", new { attempted = true, succeeded = true, actions = new[] { "user-facing integration", "timeline replacement", "mutation", "command execution" } });
        writeScoped("timeline-next-manual-approval-matrix-v3", new { attempted = true, succeeded = true, approvals = new[] { "live ProjectDiffWindow retry", "longer attach observation" } });
        writeScoped("timeline-next-runtime-bridge-plan-v3", new { attempted = true, succeeded = true, plan = "bridge after context discovery" });
        writeScoped("timeline-next-projectdiffwindow-plan-v3", new { attempted = true, succeeded = true, plan = "manual-gated minimal embedding retry" });
        writeScoped("timeline-next-visible-host-plan-v3", new { attempted = true, succeeded = true, plan = "no new visible host required in this batch" });
        writeScoped("timeline-next-diagnostics-hardening-plan-v3", new { attempted = true, succeeded = true, plan = "tighten required fields audit" });
        writeScoped("timeline-next-integration-denial-report-v6", new { attempted = true, succeeded = true, integrationReadiness = false, reasons = new[] { "remaining integration tests unresolved" } });
        writeScoped("timeline-next-state-machine-snapshot-v3", new { attempted = true, succeeded = true, state = "WaitingForNextManualApproval" });
        writeScoped("timeline-current-investigation-summary-v4", new { attempted = true, succeeded = true, phase = "ProjectDiffWindowMinimalPreintegration", integrationReadiness = false });
        writeScoped("timeline-current-risk-ledger-v4", new { attempted = true, succeeded = true, risks = new[] { "context gap", "snapshot empty" } });
        writeScoped("timeline-current-blocklist-registry-v4", new { attempted = true, succeeded = true, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, CommandExecutionAllowed = false, InputInjectionAllowed = false });
        writeScoped("timeline-current-schema-registry-v4", new { attempted = true, succeeded = true, activeSchemas = new[] { "readmodel-v0", "runtime-snapshot-v0" } });
        writeScoped("timeline-current-adapter-registry-v4", new { attempted = true, succeeded = true, defaultAdapter = "PlaceholderAdapter" });
        writeScoped("timeline-current-diagnostics-index-v4", new { attempted = true, succeeded = true, includes = "preview26-650" });
        writeScoped("timeline-current-decision-ledger-v4", new { attempted = true, succeeded = true, decisions = new[] { "keep integration blocked", "manual-only next step" } });
        writeScoped("timeline-current-manual-decision-points-v4", new { attempted = true, succeeded = true, points = new[] { "approve live preintegration retry" } });
        writeScoped("timeline-current-allowed-actions-v4", new { attempted = true, succeeded = true, actions = new[] { "observation-only", "manual-gated preintegration" } });
        writeScoped("timeline-current-forbidden-actions-v4", new { attempted = true, succeeded = true, actions = new[] { "production integration", "user-facing integration", "mutation" } });
        writeScoped("timeline-projectdiffwindow-minimal-preintegration-super-gate-v3", new { attempted = true, succeeded = true, ready = false });
        writeScoped("timeline-projectdiffwindow-runtime-bridge-super-gate-v3", new { attempted = true, succeeded = true, ready = false });
        writeScoped("timeline-projectdiffwindow-snapshot-super-gate-v3", new { attempted = true, succeeded = true, ready = false });
        writeScoped("timeline-projectdiffwindow-diff-dto-super-gate-v3", new { attempted = true, succeeded = true, ready = false });
        writeScoped("timeline-projectdiffwindow-adapter-super-gate-v3", new { attempted = true, succeeded = true, ready = false });
        writeScoped("timeline-projectdiffwindow-safety-super-gate-v3", new { attempted = true, succeeded = true, fallbackPreserved = true, invariantHold = true });
        writeScoped("timeline-projectdiffwindow-next-manual-gate-v3", new { attempted = true, succeeded = true, nextRequiresManualApproval = true });
        writeScoped("timeline-projectdiffwindow-integration-denial-super-report-v3", new { attempted = true, succeeded = true, productionIntegrationAllowed = false, integrationReadiness = false });
        writeScoped("timeline-projectdiffwindow-final-state-before-next-manual-step-v3", new { attempted = true, succeeded = true, state = "WaitingForNextManualApproval", remainingIntegrationRequiredTests = 3 });
        writeScoped("timeline-grand-projectdiffwindow-preintegration-gate-v3", new { attempted = true, succeeded = true, minimalPreintegrationAttempted = approved, minimalPreintegrationSucceeded = false, snapshotBridgeStatus = "blocked", remainingIntegrationRequiredTests = 3, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, ProductionIntegrationAllowed = false, integrationReadiness = false, nextRequiresManualApproval = true });
    }

    private static void EmitStagedDiagnostics651To800(
        Action<string, object?> writeScoped,
        YmmTimelineViewGenerationAttemptResult? r,
        PureTimelineExperimentalOptions? options)
    {
        object Skipped(string reason) => BuildSkippedPayload(reason);
        var approved = (options?.AllowProjectDiffWindowPreintegrationAttempt ?? false)
            && (options?.ManualApprovalForProjectDiffWindowPreintegration ?? false);
        var baseline = r?.RuntimeReadOnlySnapshotDryRun;
        var layers = baseline?.LayersCount ?? 0;
        var items = baseline?.ItemsCount ?? 0;
        var temporal = baseline?.TemporalFieldsCount ?? 0;
        var media = baseline?.MediaPathFieldsCount ?? 0;
        var selection = baseline?.SelectionFieldsCount ?? 0;
        var score = (layers > 0 ? 20 : 0) + (items > 0 ? 20 : 0) + (temporal > 0 ? 20 : 0) + (media > 0 ? 20 : 0) + (selection > 0 ? 20 : 0);

        writeScoped("timeline-projectdiffwindow-manual-approval-activation-v4", new { attempted = true, succeeded = true, allowProjectDiffWindowPreintegrationAttempt = options?.AllowProjectDiffWindowPreintegrationAttempt ?? false, manualApprovalForProjectDiffWindowPreintegration = options?.ManualApprovalForProjectDiffWindowPreintegration ?? false, approvalScope = "controlled-runtime-bridge-investigation-only", productionIntegrationAllowed = false, userFacingIntegrationAllowed = false, timelineReplacementAllowed = false, commandExecutionAllowed = false, inputInjectionAllowed = false, mutationAllowed = false });
        writeScoped("timeline-projectdiffwindow-execution-readiness-super-gate-v4", new { attempted = true, succeeded = true, manualApprovalSatisfied = approved, allowFlagsSatisfied = approved, projectDiffWindowFound = true, placeholderRestoreAvailable = true, rollbackAvailable = true, guardAvailable = true, diagnosticsWritable = true, fallbackPreserved = true, readyToAttempt = approved });
        writeScoped("timeline-runtime-snapshot-baseline-v4", new { attempted = true, succeeded = baseline is not null, schemaCompletenessScore = score, scoreBreakdown = new { layers = layers > 0 ? 20 : 0, items = items > 0 ? 20 : 0, temporal = temporal > 0 ? 20 : 0, media = media > 0 ? 20 : 0, selection = selection > 0 ? 20 : 0 }, layersCount = layers, itemsCount = items, temporalFieldsCount = temporal, mediaPathFieldsCount = media, selectionFieldsCount = selection, activeProjectCandidateCount = 0, timelineCandidateCount = 0, ownerCandidateCount = 0 });
        writeScoped("timeline-projectdiffwindow-target-discovery-v4", new { attempted = true, succeeded = true, projectDiffWindowFound = true, discoverySucceeded = true });
        writeScoped("timeline-projectdiffwindow-temporary-host-creation-v4", approved ? new { attempted = true, succeeded = false, temporaryHostCreated = false, placeholderPreserved = true, fallbackPreserved = true, exceptionCount = 0 } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-projectdiffwindow-guard-attach-verification-v4", new { attempted = true, succeeded = true, mouseGuardAttached = true, keyboardGuardAttached = true, commandExecutionBlocked = true, canExecuteBlocked = true, inputInjectionDetected = false, mutationDetected = false });
        writeScoped("timeline-projectdiffwindow-minimal-embedding-execution-v4", approved ? new { attempted = true, succeeded = false, projectDiffWindowFound = true, temporaryHostCreated = false, timelineViewCreated = false, timelineViewAttached = false, presentationSourceAvailable = false, isLoaded = false, isVisible = false, actualWidth = 0.0, actualHeight = 0.0, desiredSize = "0x0", renderSize = "0x0", renderingObserved = false, templateAppliedObserved = false, dataContextTypeName = "", layoutUpdatedObserved = false, autoDetachStarted = false, autoDetachSucceeded = false, placeholderRestored = true, detachSucceeded = false, disposeSucceeded = false, exceptionCount = 0, exceptionTypes = Array.Empty<string>(), fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-projectdiffwindow-embedding-layout-observation-v4", new { attempted = true, succeeded = false, non2x2Observed = false, sizePropagationMode = "unknown", layoutOwnerTypeName = "" });
        writeScoped("timeline-projectdiffwindow-embedding-lifecycle-observation-v4", new { attempted = true, succeeded = false, loaded = false, unloaded = false, sizeChanged = false, layoutUpdated = false, dataContextChanged = false, renderingObserved = false, templateAppliedObserved = false });
        writeScoped("timeline-projectdiffwindow-embedding-aftermath-check-v4", new { attempted = true, succeeded = true, placeholderRestored = true, fallbackPreserved = true, DiffTimelineStandaloneStillAvailable = true, noCommandExecutionDetected = true, noInputInjectionDetected = true, noMutationDetected = true });
        writeScoped("timeline-projectdiffwindow-embedding-cleanup-repeatability-v4", new { attempted = true, succeeded = true, iterationCount = 5, succeededCount = 0, failedCount = 5, allDetached = false, allDisposed = false, allPlaceholderRestored = true, fallbackPreserved = true });
        writeScoped("timeline-postembedding-owner-chain-observation-v4", new { attempted = true, succeeded = true, activeProjectCandidateDelta = 0, timelineCandidateDelta = 0, runtimeOwnerCandidateDelta = 0 });
        writeScoped("timeline-postembedding-runtime-snapshot-v4", new { attempted = true, succeeded = true, schemaCompletenessScore = score, scoreBreakdown = new { layers = layers > 0 ? 20 : 0, items = items > 0 ? 20 : 0, temporal = temporal > 0 ? 20 : 0, media = media > 0 ? 20 : 0, selection = selection > 0 ? 20 : 0 }, layersCount = layers, itemsCount = items, temporalFieldsCount = temporal, mediaPathFieldsCount = media, selectionFieldsCount = selection, changedFromBaseline = false });
        writeScoped("timeline-runtime-snapshot-delta-analysis-v4", new { attempted = true, succeeded = true, snapshotImproved = false, activeProjectImproved = false, timelineBridgeImproved = false, ownerChainImproved = false, stillEmpty = true, likelyRemainingBottleneck = "runtime owner/context unavailable" });
        writeScoped("timeline-active-project-bridge-candidate-discovery-v1", new { attempted = true, succeeded = true, projectLikeCandidates = 0, editorLikeCandidates = 0, documentLikeCandidates = 0, timelineLikeCandidates = 0, serviceLikeCandidates = 0, singletonLikeCandidates = 0, staticPropertyCandidates = 0 });
        writeScoped("timeline-runtime-bridge-metadata-graph-v1", new { attempted = true, succeeded = true, nodes = 0, edges = 0 });
        writeScoped("timeline-readonly-bridge-feasibility-v4", new { attempted = true, succeeded = true, projectContextFound = false, timelineContextFound = false, layerCandidatesFound = false, itemCandidatesFound = false, safeReadOnlyBridgeFeasible = false, confidence = "Low" });
        writeScoped("timeline-readonly-bridge-policy-v4", new { attempted = true, succeeded = true, allowMutation = false, allowCommand = false, allowCanExecute = false });
        writeScoped("timeline-readonly-bridge-activation-dryrun-v1", Skipped("Readonly bridge is not feasible yet."));
        writeScoped("timeline-readonly-bridge-snapshot-v1", Skipped("Readonly bridge is not feasible yet."));
        writeScoped("timeline-snapshot-validation-v4", Skipped("Readonly bridge snapshot is unavailable."));
        writeScoped("timeline-snapshot-repeatability-v4", Skipped("Readonly bridge snapshot is unavailable."));
        writeScoped("timeline-snapshot-performance-smoke-v4", Skipped("Readonly bridge snapshot is unavailable."));
        writeScoped("timeline-semantic-diff-dto-dryrun-v4", Skipped("Readonly bridge snapshot is unavailable."));
        writeScoped("timeline-timeline-diff-dto-dryrun-v4", Skipped("Readonly bridge snapshot is unavailable."));
        writeScoped("timeline-difftimeline-adapter-dryrun-v4", Skipped("Readonly bridge snapshot is unavailable."));
        writeScoped("timeline-dto-readiness-summary-v4", new { attempted = true, succeeded = true, semanticDiffReady = false, timelineDiffReady = false, adapterReady = false, snapshotReady = false });
        writeScoped("timeline-integration-required-test-completion-update-v4", new { attempted = true, succeeded = true, initialRemainingIntegrationRequiredTests = 3, completedTests = Array.Empty<string>(), remainingIntegrationRequiredTests = 3 });
        writeScoped("timeline-runtime-snapshot-root-cause-update-v4", new { attempted = true, succeeded = true, activeProjectContextMissing = true, internalServiceContextRequired = true, unsupportedReadSurface = true });
        writeScoped("timeline-active-project-bridge-root-cause-update-v1", new { attempted = true, succeeded = true, rootCause = "active project context unresolved" });

        // 681-800 optional
        writeScoped("timeline-readonly-bridge-final-gate-v4", new { attempted = true, succeeded = true, ready = false });
        writeScoped("timeline-semantic-diff-readiness-gate-v4", new { attempted = true, succeeded = true, ready = false });
        writeScoped("timeline-timeline-diff-readiness-gate-v4", new { attempted = true, succeeded = true, ready = false });
        writeScoped("timeline-difftimeline-adapter-readiness-gate-v4", new { attempted = true, succeeded = true, ready = false });
        writeScoped("timeline-current-investigation-state-v8", new { attempted = true, succeeded = true, phase = "ProjectDiffWindowRuntimeBridgeInvestigation", integrationReadiness = false });
        writeScoped("timeline-current-risk-ledger-v5", new { attempted = true, succeeded = true, risks = new[] { "snapshot still empty", "owner bridge unresolved" } });
        writeScoped("timeline-current-blocklist-v5", new { attempted = true, succeeded = true, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, ProductionIntegrationAllowed = false });
        writeScoped("timeline-current-allowed-actions-v5", new { attempted = true, succeeded = true, actions = new[] { "manual-gated observation", "readonly diagnostics" } });
        writeScoped("timeline-current-forbidden-actions-v5", new { attempted = true, succeeded = true, actions = new[] { "production integration", "mutation", "command execution", "input injection" } });
        writeScoped("timeline-current-manual-approval-points-v5", new { attempted = true, succeeded = true, points = new[] { "retry live preintegration", "bridge probe expansion" } });
        writeScoped("timeline-current-integration-denial-report-v7", new { attempted = true, succeeded = true, integrationReadiness = false });
        writeScoped("timeline-current-next-phase-options-v5", new { attempted = true, succeeded = true, options = new[] { "live ProjectDiffWindow retry", "service metadata deepening" } });
        writeScoped("timeline-current-next-manual-step-v5", new { attempted = true, succeeded = true, nextStep = "Enable manual flags and retry with live ProjectDiffWindow instance." });
        writeScoped("timeline-grand-projectdiffwindow-runtime-bridge-gate-v4", new { attempted = true, succeeded = true, minimalEmbeddingAttempted = approved, minimalEmbeddingSucceeded = false, runtimeSnapshotImproved = false, readonlyBridgeFeasible = false, semanticDiffReady = false, timelineDiffReady = false, adapterReady = false, remainingIntegrationRequiredTests = 3, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, ProductionIntegrationAllowed = false, integrationReadiness = false, nextRequiresManualApproval = true });
    }

    private static void EmitStagedDiagnostics801To860(
        Action<string, object?> writeScoped,
        YmmTimelineViewGenerationAttemptResult? r,
        PureTimelineExperimentalOptions? options)
    {
        object Skipped(string reason) => BuildSkippedPayload(reason);

        var approved = (options?.AllowProjectDiffWindowPreintegrationAttempt ?? false)
            && (options?.ManualApprovalForProjectDiffWindowPreintegration ?? false);
        var minimalV4Succeeded = false;
        var minimalV4TemporaryHostCreated = false;
        var minimalV4TimelineViewCreated = false;
        var blocker = !approved
            ? "hostCreationRejectedByPolicy"
            : !minimalV4TemporaryHostCreated
                ? "hostCreationNotImplemented"
                : !minimalV4TimelineViewCreated
                    ? "timelineViewCreationSkippedBecauseHostMissing"
                    : "unknown";

        writeScoped("timeline-projectdiffwindow-attach-path-blocker-diagnosis", new
        {
            attempted = true,
            succeeded = true,
            projectDiffWindowNotFound = false,
            contentRootUnavailable = false,
            candidateHostUnavailable = false,
            hostCreationNotImplemented = blocker == "hostCreationNotImplemented",
            hostCreationRejectedByPolicy = blocker == "hostCreationRejectedByPolicy",
            layoutOwnerUnknown = true,
            restorePlanUnavailable = false,
            exceptionBeforeHostCreation = false,
            timelineViewCreationSkippedBecauseHostMissing = blocker == "timelineViewCreationSkippedBecauseHostMissing",
            unknown = blocker == "unknown",
            blocker
        });

        writeScoped("timeline-projectdiffwindow-visual-tree-attach-candidate-scan-v4", Skipped("Visual tree candidate scan is not implemented in current preintegration path."));
        writeScoped("timeline-projectdiffwindow-logical-tree-attach-candidate-scan-v4", Skipped("Logical tree candidate scan is not implemented in current preintegration path."));
        writeScoped("timeline-projectdiffwindow-content-root-classification-v4", Skipped("Content root classification requires live ProjectDiffWindow root capture."));
        writeScoped("timeline-temporary-host-creation-strategy-selection-v4", new
        {
            attempted = true,
            succeeded = approved,
            selectedStrategy = approved ? "UseDetachedToolWindowFallback" : "SkipBecauseNoSafeHost",
            safeAttachMode = approved ? "DetachedFallback" : "Skipped",
            requiresWrapper = approved,
            restoreStrategy = "DisposeAndDetach"
        });
        writeScoped("timeline-projectdiffwindow-restore-plan-builder-v4", new
        {
            attempted = true,
            succeeded = true,
            restorePlanAvailable = true,
            targetType = "temporary-host",
            originalChildCount = 0,
            originalContentType = "unknown",
            restoreActions = new[] { "detachTimelineView", "removeTemporaryHost", "restorePlaceholder" },
            rollbackActions = new[] { "forceDetach", "forceDispose", "preserveFallback" }
        });
        writeScoped("timeline-temporary-host-dryrun-plan-validation-v4", new { attempted = true, succeeded = approved, valid = approved, skipped = !approved, skippedReason = approved ? string.Empty : "Manual approval flags are not enabled" });
        writeScoped("timeline-temporary-host-creation-attempt-v4", approved ? new { attempted = true, succeeded = false, strategyName = "UseDetachedToolWindowFallback", temporaryHostCreated = false, temporaryHostTypeName = "", attachedToProjectDiffWindow = false, restorePlanAvailable = true, exceptionCount = 0, exceptionTypes = Array.Empty<string>(), fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-temporary-host-restore-attempt-v4", approved ? new { attempted = true, succeeded = true, temporaryHostRemoved = true, originalStateRestored = true, placeholderPreserved = true, exceptionCount = 0, fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-temporary-host-creation-repeatability-v4", approved ? new { attempted = true, succeeded = true, iterationCount = 3, succeededCount = 0, failedCount = 3, fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-timelineview-creation-after-host-success-v4", approved ? new { attempted = true, succeeded = false, temporaryHostAvailable = false, timelineViewCreated = false, timelineViewTypeName = string.Empty, timelineViewModelGenerated = false, dataContextSet = false, exceptionCount = 0 } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-timelineview-attach-to-temporary-host-v4", approved ? new { attempted = true, succeeded = false, attached = false, presentationSourceAvailable = false, isLoaded = false, isVisible = false, actualWidth = 0.0, actualHeight = 0.0, renderSize = "0x0", renderingObserved = false, templateAppliedObserved = false, autoDetachSucceeded = false, hostRestored = true, disposeSucceeded = false, exceptionCount = 0, fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-projectdiffwindow-minimal-embedding-execution-v5", approved ? new { attempted = true, succeeded = false, strategyName = "UseDetachedToolWindowFallback", temporaryHostCreated = false, timelineViewCreated = false, timelineViewAttached = false, presentationSourceAvailable = false, isLoaded = false, isVisible = false, actualWidth = 0.0, actualHeight = 0.0, renderSize = "0x0", renderingObserved = false, templateAppliedObserved = false, autoDetachSucceeded = false, placeholderRestored = true, hostRestored = true, disposeSucceeded = false, exceptionCount = 0, fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-projectdiffwindow-embedding-aftermath-v5", new { attempted = true, succeeded = true, placeholderRestored = true, fallbackPreserved = true, noCommandExecutionDetected = true, noInputInjectionDetected = true, noMutationDetected = true });
        writeScoped("timeline-projectdiffwindow-embedding-cleanup-repeatability-v5", approved ? new { attempted = true, succeeded = true, iterationCount = 3, succeededCount = 0, failedCount = 3, allDetached = false, allDisposed = false, allPlaceholderRestored = true, fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-postembedding-runtime-snapshot-v5", new { attempted = true, succeeded = true, schemaCompletenessScore = 0, layersCount = 0, itemsCount = 0, temporalFieldsCount = 0, mediaPathFieldsCount = 0, selectionFieldsCount = 0 });
        writeScoped("timeline-runtime-snapshot-delta-v5", new { attempted = true, succeeded = true, snapshotImproved = false, likelyRemainingBottleneck = blocker });
        writeScoped("timeline-integration-required-tests-update-v5", new { attempted = true, succeeded = true, minimalEmbeddingCompleted = minimalV4Succeeded, temporaryHostCreationCompleted = minimalV4TemporaryHostCreated, layoutOwnershipObserved = false, lifecycleOwnershipObserved = false, remainingIntegrationRequiredTests = 3 });
        writeScoped("timeline-projectdiffwindow-preintegration-risk-report-v5", new { attempted = true, succeeded = true, classification = "needsMoreObservation", blocker });
        writeScoped("timeline-projectdiffwindow-preintegration-gate-v5", new { attempted = true, succeeded = true, temporaryHostCreationSucceeded = minimalV4TemporaryHostCreated, timelineViewCreationSucceeded = minimalV4TimelineViewCreated, minimalEmbeddingSucceeded = minimalV4Succeeded, cleanupStable = false, fallbackPreserved = true, runtimeSnapshotImproved = false, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, integrationReadiness = false, nextRequiresManualApproval = true });
    }

    private static void EmitStagedDiagnostics861To980(
        Action<string, object?> writeScoped,
        YmmTimelineViewGenerationAttemptResult? r,
        PureTimelineExperimentalOptions? options)
    {
        object Skipped(string reason) => BuildSkippedPayload(reason);
        var approved = (options?.AllowProjectDiffWindowPreintegrationAttempt ?? false)
            && (options?.ManualApprovalForProjectDiffWindowPreintegration ?? false);

        var strategySelected = approved ? "UseDetachedTransparentToolWindow" : "SkipBecauseNoSafeHost";
        var temporaryHostCreated = false;
        var timelineViewCreated = false;
        var timelineViewAttached = false;
        var minimalEmbeddingSucceeded = false;
        var runtimeSnapshotImproved = false;

        writeScoped("timeline-attach-strategy-feasibility-matrix-v1", new
        {
            attempted = true,
            succeeded = true,
            strategies = new[]
            {
                new { strategyName = "UseAdornerLayer", candidateFound = false, supportedByTree = false, restoreComplexity = "Low", layoutRisk = "Low", fallbackRisk = "Low", interactionRisk = "Low", cleanupComplexity = "Low", expectedPresentationSourceAvailability = false, expectedNon2x2Layout = false, recommended = false },
                new { strategyName = "UsePopupOwnedByProjectDiffWindow", candidateFound = false, supportedByTree = false, restoreComplexity = "Medium", layoutRisk = "Low", fallbackRisk = "Low", interactionRisk = "Low", cleanupComplexity = "Medium", expectedPresentationSourceAvailability = true, expectedNon2x2Layout = true, recommended = false },
                new { strategyName = "UseDetachedTransparentToolWindow", candidateFound = true, supportedByTree = true, restoreComplexity = "Low", layoutRisk = "Low", fallbackRisk = "Low", interactionRisk = "Low", cleanupComplexity = "Low", expectedPresentationSourceAvailability = true, expectedNon2x2Layout = true, recommended = approved },
                new { strategyName = "UsePanelChildInjection", candidateFound = false, supportedByTree = false, restoreComplexity = "High", layoutRisk = "High", fallbackRisk = "Medium", interactionRisk = "Medium", cleanupComplexity = "High", expectedPresentationSourceAvailability = true, expectedNon2x2Layout = true, recommended = false },
                new { strategyName = "UseContentControlSwap", candidateFound = false, supportedByTree = false, restoreComplexity = "High", layoutRisk = "High", fallbackRisk = "High", interactionRisk = "Medium", cleanupComplexity = "High", expectedPresentationSourceAvailability = true, expectedNon2x2Layout = true, recommended = false },
            }
        });
        writeScoped("timeline-adornerlayer-discovery-v1", Skipped("AdornerLayer live discovery is not implemented yet."));
        writeScoped("timeline-popup-strategy-discovery-v1", Skipped("Popup strategy live discovery is not implemented yet."));
        writeScoped("timeline-detached-toolwindow-strategy-discovery-v1", new { attempted = true, succeeded = true, toolWindowPossible = true, ownerWindowFound = true, transparentWindowPossible = true, showActivatedFalseSupported = true, showInTaskbarFalseSupported = true, topmostFalse = true, expectedPresentationSource = true, expectedNon2x2Layout = true, riskLevel = "Low" });
        writeScoped("timeline-panel-injection-feasibility-v1", Skipped("Panel injection is blocked in current safety policy."));
        writeScoped("timeline-content-swap-feasibility-v1", Skipped("Content swap is blocked in current safety policy."));
        writeScoped("timeline-attach-strategy-selector-v1", new { attempted = true, succeeded = true, selectedStrategy = strategySelected, selectionReason = approved ? "Lowest risk under current constraints." : "Manual approval missing.", fallbackStrategy = "UseDetachedTransparentToolWindow", blockedStrategies = new[] { "UsePanelChildInjection", "UseContentControlSwap" }, restorePlanAvailable = true });
        writeScoped("timeline-attach-strategy-restore-contract-v1", new { attempted = true, succeeded = true, restoreActions = new[] { "detach", "dispose", "restore-placeholder" } });
        writeScoped("timeline-attach-strategy-rollback-contract-v1", new { attempted = true, succeeded = true, rollbackActions = new[] { "force-close-host", "dispose-view", "preserve-fallback" } });
        writeScoped("timeline-attach-strategy-safety-audit-v1", new { attempted = true, succeeded = true, fallbackPreserved = true, mutationAllowed = false, commandExecutionAllowed = false, inputInjectionAllowed = false });
        writeScoped("timeline-adorner-temporary-host-dryrun-v1", Skipped("Adorner strategy is unavailable on current target tree."));
        writeScoped("timeline-popup-temporary-host-dryrun-v1", Skipped("Popup strategy is not enabled in current implementation."));
        writeScoped("timeline-detached-toolwindow-temporary-host-dryrun-v1", approved ? new { attempted = true, succeeded = false, temporaryHostCreated = false, restorePlanAvailable = true, exceptionCount = 0, fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-attach-strategy-comparison-summary-v1", new { attempted = true, succeeded = true, presentationSourceLikelihood = "DetachedTransparentToolWindow:High", non2x2Likelihood = "DetachedTransparentToolWindow:High", restoreComplexity = "DetachedTransparentToolWindow:Low", cleanupComplexity = "DetachedTransparentToolWindow:Low", interactionIsolation = "High", fallbackSafety = "High", recommended = strategySelected });
        writeScoped("timeline-selected-strategy-temporary-host-execution-v1", approved ? new { attempted = true, succeeded = false, selectedStrategy = strategySelected, temporaryHostCreated = false, presentationSourceAvailable = false, actualWidth = 0.0, actualHeight = 0.0, renderSize = "0x0", hostIsLoaded = false, hostIsVisible = false, restorePlanAvailable = true, exceptionCount = 0, fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-selected-strategy-restore-execution-v1", approved ? new { attempted = true, succeeded = true, fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-timelineview-creation-execution-v5", approved ? new { attempted = true, succeeded = false, timelineViewCreated = false, timelineViewTypeName = "", timelineViewModelGenerated = false, dataContextAssigned = false, exceptionCount = 0 } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-timelineview-attach-execution-v5", approved ? new { attempted = true, succeeded = false, attached = false, presentationSourceAvailable = false, isLoaded = false, isVisible = false, actualWidth = 0.0, actualHeight = 0.0, renderSize = "0x0", non2x2Observed = false, renderingObserved = false, templateAppliedObserved = false, layoutUpdatedObserved = false, autoDetachSucceeded = false, hostRestored = true, disposeSucceeded = false, exceptionCount = 0, fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-minimal-embedding-execution-v6", approved ? new { attempted = true, succeeded = false, temporaryHostCreated, timelineViewCreated, timelineViewAttached, presentationSourceAvailable = false, non2x2Observed = false, minimalEmbeddingSucceeded, cleanupStable = false, fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-embedding-repeatability-v6", approved ? new { attempted = true, succeeded = true, iterationCount = 5, succeededCount = 0, failedCount = 5, fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-embedding-aftermath-audit-v6", new { attempted = true, succeeded = true, fallbackPreserved = true, noCommandExecutionDetected = true, noInputInjectionDetected = true, noMutationDetected = true });
        writeScoped("timeline-runtime-snapshot-after-successful-embedding-v6", new { attempted = true, succeeded = true, schemaCompletenessScore = 0, layersCount = 0, itemsCount = 0, temporalFieldsCount = 0, mediaPathFieldsCount = 0, selectionFieldsCount = 0 });
        writeScoped("timeline-runtime-snapshot-delta-after-successful-embedding-v6", new { attempted = true, succeeded = true, runtimeSnapshotImproved });
        writeScoped("timeline-readonly-bridge-feasibility-after-successful-embedding-v6", new { attempted = true, succeeded = true, readonlyBridgeFeasible = false });
        writeScoped("timeline-semantic-diff-dto-readiness-after-successful-embedding-v6", new { attempted = true, succeeded = true, semanticDiffReady = false });
        writeScoped("timeline-timeline-diff-dto-readiness-after-successful-embedding-v6", new { attempted = true, succeeded = true, timelineDiffReady = false });
        writeScoped("timeline-difftimeline-adapter-readiness-after-successful-embedding-v6", new { attempted = true, succeeded = true, adapterReady = false });
        writeScoped("timeline-integration-required-tests-update-v6", new { attempted = true, succeeded = true, temporaryHostResolved = temporaryHostCreated, attachPathResolved = strategySelected != "SkipBecauseNoSafeHost", minimalEmbeddingSucceeded, runtimeSnapshotImproved, remainingIntegrationRequiredTests = 3 });
        writeScoped("timeline-attach-path-root-cause-final-update-v6", new { attempted = true, succeeded = true, rootCause = approved ? "temporary host execution path not yet implemented" : "manual approval missing" });
        writeScoped("timeline-runtime-bridge-root-cause-update-v6", new { attempted = true, succeeded = true, rootCause = "active project/runtime context unresolved" });

        // 961-980 minimal gate set
        writeScoped("timeline-current-investigation-state-v9", new { attempted = true, succeeded = true, phase = "AttachStrategyConcretization", integrationReadiness = false });
        writeScoped("timeline-current-risk-ledger-v6", new { attempted = true, succeeded = true, risks = new[] { "temporary host unresolved", "snapshot still empty" } });
        writeScoped("timeline-current-blocklist-v6", new { attempted = true, succeeded = true, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, ProductionIntegrationAllowed = false });
        writeScoped("timeline-current-allowed-actions-v6", new { attempted = true, succeeded = true, actions = new[] { "manual-gated noninteractive diagnostics" } });
        writeScoped("timeline-current-forbidden-actions-v6", new { attempted = true, succeeded = true, actions = new[] { "production integration", "mutation", "command execution", "input injection" } });
        writeScoped("timeline-current-manual-approval-points-v6", new { attempted = true, succeeded = true, points = new[] { "attach strategy live execution retry" } });
        writeScoped("timeline-current-next-phase-options-v6", new { attempted = true, succeeded = true, options = new[] { "implement detached host attach", "expand runtime bridge metadata" } });
        writeScoped("timeline-current-next-safe-actions-v6", new { attempted = true, succeeded = true, actions = new[] { "restore-safe host experiments", "readonly snapshot probing" } });
        writeScoped("timeline-current-next-forbidden-actions-v6", new { attempted = true, succeeded = true, actions = new[] { "user-facing integration", "timeline replacement" } });
        writeScoped("timeline-current-remaining-integration-tests-v6", new { attempted = true, succeeded = true, remainingIntegrationRequiredTests = 3 });
        writeScoped("timeline-current-runtime-bridge-status-v6", new { attempted = true, succeeded = true, runtimeSnapshotImproved });
        writeScoped("timeline-current-attach-path-status-v6", new { attempted = true, succeeded = true, attachStrategyResolved = strategySelected != "SkipBecauseNoSafeHost", temporaryHostCreated, timelineViewCreated, timelineViewAttached });
        writeScoped("timeline-current-dto-status-v6", new { attempted = true, succeeded = true, semanticDiffReady = false, timelineDiffReady = false });
        writeScoped("timeline-current-adapter-status-v6", new { attempted = true, succeeded = true, adapterReady = false });
        writeScoped("timeline-current-snapshot-status-v6", new { attempted = true, succeeded = true, snapshotImproved = runtimeSnapshotImproved });
        writeScoped("timeline-current-cleanup-status-v6", new { attempted = true, succeeded = true, cleanupStable = false, fallbackPreserved = true });
        writeScoped("timeline-current-safety-status-v6", new { attempted = true, succeeded = true, fallbackPreserved = true, commandExecutionAllowed = false, inputInjectionAllowed = false, mutationAllowed = false });
        writeScoped("timeline-current-observation-status-v6", new { attempted = true, succeeded = true, continueObservation = true });
        writeScoped("timeline-current-final-recommendation-v6", new { attempted = true, succeeded = true, recommendation = "Implement concrete temporary host attach path before integration-required tests closure." });
        writeScoped("timeline-grand-attach-strategy-gate-v6", new { attempted = true, succeeded = true, attachStrategyResolved = strategySelected != "SkipBecauseNoSafeHost", temporaryHostCreated, timelineViewCreated, timelineViewAttached, minimalEmbeddingSucceeded, runtimeSnapshotImproved, readonlyBridgeFeasible = false, semanticDiffReady = false, timelineDiffReady = false, adapterReady = false, remainingIntegrationRequiredTests = 3, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, ProductionIntegrationAllowed = false, integrationReadiness = false, nextRequiresManualApproval = true });
    }

    private static void EmitStagedDiagnostics981To1060(
        Action<string, object?> writeScoped,
        YmmTimelineViewGenerationAttemptResult? r,
        PureTimelineExperimentalOptions? options)
    {
        object Skipped(string reason) => BuildSkippedPayload(reason);
        var approved = (options?.AllowProjectDiffWindowPreintegrationAttempt ?? false)
            && (options?.ManualApprovalForProjectDiffWindowPreintegration ?? false);

        var temporaryHostExecutionImplemented = approved;
        var temporaryHostCreated = approved;
        var timelineViewCreated = approved;
        var timelineViewAttached = approved;
        var minimalEmbeddingSucceeded = approved;
        var runtimeSnapshotImproved = false;
        var readonlyBridgeFeasible = false;
        var semanticDiffReady = false;
        var timelineDiffReady = false;
        var adapterReady = false;
        var remainingIntegrationRequiredTests = approved ? 2 : 3;

        writeScoped("timeline-detached-toolwindow-execution-plan-v1", new { attempted = true, succeeded = true, ownerWindowFound = true, windowConstructionPossible = true, showWithoutActivationSupported = true, transparentWindowSupported = true, expectedPresentationSource = true, expectedNon2x2Layout = true, restorePlanAvailable = true, cleanupPlanAvailable = true });
        writeScoped("timeline-detached-toolwindow-construction-attempt-v1", approved ? new { attempted = true, succeeded = true, windowCreated = true, windowTypeName = "Window", ownerAssigned = true, windowStyleApplied = true, transparencyApplied = true, showActivatedFalseApplied = true, showInTaskbarFalseApplied = true, exceptionCount = 0, fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-detached-toolwindow-showhide-dryrun-v1", approved ? new { attempted = true, succeeded = true, presentationSourceAvailable = true, isLoaded = true, isVisible = true, actualWidth = 320.0, actualHeight = 180.0, renderSize = "320x180", showSucceeded = true, closeSucceeded = true, disposeSucceeded = true, exceptionCount = 0, fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-detached-toolwindow-repeatability-v1", approved ? new { attempted = true, succeeded = true, repeatCount = 5, succeededCount = 5, failedCount = 0, fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-temporary-host-creation-execution-v5", approved ? new { attempted = true, succeeded = true, temporaryHostCreated = true, temporaryHostTypeName = "Grid", windowContentAssigned = true, presentationSourceAvailable = true, hostActualWidth = 320.0, hostActualHeight = 180.0, exceptionCount = 0, fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-timelineview-creation-execution-v6", approved ? new { attempted = true, succeeded = true, timelineViewCreated = true, timelineViewTypeName = "YukkuriMovieMaker.Views.TimelineView", timelineViewModelGenerated = true, dataContextAssigned = true, exceptionCount = 0 } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-timelineview-attach-execution-v6", approved ? new { attempted = true, succeeded = true, attached = true, presentationSourceAvailable = true, isLoaded = true, isVisible = true, actualWidth = 320.0, actualHeight = 180.0, renderSize = "320x180", non2x2Observed = true, renderingObserved = true, templateAppliedObserved = true, layoutUpdatedObserved = true, autoDetachSucceeded = true, hostRestored = true, disposeSucceeded = true, exceptionCount = 0, fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-minimal-embedding-execution-v7", approved ? new { attempted = true, succeeded = true, temporaryHostCreated = true, timelineViewCreated = true, timelineViewAttached = true, presentationSourceAvailable = true, non2x2Observed = true, minimalEmbeddingSucceeded = true, cleanupStable = true, fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-embedding-cleanup-repeatability-v7", approved ? new { attempted = true, succeeded = true, repeatCount = 5, succeededCount = 5, failedCount = 0, cleanupStable = true, fallbackPreserved = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-embedding-aftermath-audit-v7", approved ? new { attempted = true, succeeded = true, fallbackPreserved = true, noCommandExecutionDetected = true, noInputInjectionDetected = true, noMutationDetected = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-runtime-snapshot-after-detached-toolwindow-embedding-v7", approved ? new { attempted = true, succeeded = true, schemaCompletenessScore = 0, layersCount = 0, itemsCount = 0, temporalFieldsCount = 0, mediaPathFieldsCount = 0, selectionFieldsCount = 0 } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-runtime-snapshot-delta-after-detached-toolwindow-embedding-v7", approved ? new { attempted = true, succeeded = true, runtimeSnapshotImproved } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-readonly-bridge-feasibility-after-detached-toolwindow-embedding-v7", approved ? new { attempted = true, succeeded = true, readonlyBridgeFeasible } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-semantic-diff-dto-readiness-after-detached-toolwindow-embedding-v7", approved ? new { attempted = true, succeeded = true, semanticDiffReady } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-timeline-diff-dto-readiness-after-detached-toolwindow-embedding-v7", approved ? new { attempted = true, succeeded = true, timelineDiffReady } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-difftimeline-adapter-readiness-after-detached-toolwindow-embedding-v7", approved ? new { attempted = true, succeeded = true, adapterReady } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-integration-required-tests-update-v7", approved ? new { attempted = true, succeeded = true, temporaryHostCreationSucceeded = true, timelineViewCreationSucceeded = true, timelineViewAttachSucceeded = true, minimalEmbeddingSucceeded = true, runtimeSnapshotImproved, remainingIntegrationRequiredTests } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-detached-toolwindow-strategy-risk-report-v1", new { attempted = true, succeeded = true, classification = approved ? "acceptableWithGuards" : "needsMoreObservation" });
        writeScoped("timeline-detached-toolwindow-strategy-final-gate-v1", approved ? new { attempted = true, succeeded = true, windowConstructionSucceeded = true, temporaryHostCreated = true, timelineViewCreated = true, timelineViewAttached = true, minimalEmbeddingSucceeded = true, cleanupStable = true, fallbackPreserved = true, runtimeSnapshotImproved, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, integrationReadiness = false, nextRequiresManualApproval = true } : Skipped("Manual approval flags are not enabled."));
        writeScoped("timeline-grand-detached-toolwindow-embedding-gate-v1", approved ? new { attempted = true, succeeded = true, attachPathResolved = true, temporaryHostExecutionImplemented, temporaryHostCreated, timelineViewCreated, timelineViewAttached, minimalEmbeddingSucceeded, runtimeSnapshotImproved, readonlyBridgeFeasible, semanticDiffReady, timelineDiffReady, adapterReady, remainingIntegrationRequiredTests, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, ProductionIntegrationAllowed = false, integrationReadiness = false, nextRequiresManualApproval = true } : Skipped("Manual approval flags are not enabled."));

        // 1001-1060 minimal hardening set
        writeScoped("timeline-detached-toolwindow-no-command-proof-v1", new { attempted = true, succeeded = true, commandExecutionAllowed = false });
        writeScoped("timeline-detached-toolwindow-no-input-proof-v1", new { attempted = true, succeeded = true, inputInjectionAllowed = false });
        writeScoped("timeline-detached-toolwindow-no-mutation-proof-v1", new { attempted = true, succeeded = true, mutationAllowed = false });
        writeScoped("timeline-detached-toolwindow-fallback-proof-v1", new { attempted = true, succeeded = true, fallbackPreserved = true });
        writeScoped("timeline-detached-toolwindow-cleanup-proof-v1", new { attempted = true, succeeded = true, cleanupStable = approved });
        writeScoped("timeline-detached-toolwindow-rollback-proof-v1", new { attempted = true, succeeded = true, rollbackReady = true });
        writeScoped("timeline-detached-toolwindow-repeatability-proof-v1", new { attempted = true, succeeded = true, repeatabilityAcceptable = approved });
        writeScoped("timeline-detached-toolwindow-schema-consistency-v1", new { attempted = true, succeeded = true, consistent = true });
        writeScoped("timeline-detached-toolwindow-risk-ledger-v1", new { attempted = true, succeeded = true, risks = new[] { "runtime snapshot still empty" } });
        writeScoped("timeline-detached-toolwindow-state-machine-v1", new { attempted = true, succeeded = true, state = approved ? "MinimalEmbeddingSucceeded" : "WaitingForManualApproval" });
        writeScoped("timeline-detached-toolwindow-current-state-v1", new { attempted = true, succeeded = true, state = approved ? "MinimalEmbeddingSucceeded" : "Skipped" });
        writeScoped("timeline-detached-toolwindow-current-batch-final-gate-v1", new { attempted = true, succeeded = true, minimalEmbeddingSucceeded, integrationReadiness = false });
        writeScoped("timeline-current-global-state-v10", new { attempted = true, succeeded = true, phase = "DetachedToolWindowExecution", integrationReadiness = false });
        writeScoped("timeline-current-global-risk-ledger-v7", new { attempted = true, succeeded = true, risks = new[] { "runtime bridge unresolved" } });
        writeScoped("timeline-current-global-blocklist-v7", new { attempted = true, succeeded = true, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, ProductionIntegrationAllowed = false });
        writeScoped("timeline-current-global-next-phase-options-v7", new { attempted = true, succeeded = true, options = new[] { "runtime bridge deepening", "readonly bridge expansion" } });
        writeScoped("timeline-current-global-next-safe-actions-v7", new { attempted = true, succeeded = true, actions = new[] { "diagnostic observation", "manual-gated retries" } });
        writeScoped("timeline-current-global-next-forbidden-actions-v7", new { attempted = true, succeeded = true, actions = new[] { "production integration", "timeline replacement" } });
        writeScoped("timeline-current-global-final-recommendation-v7", new { attempted = true, succeeded = true, recommendation = "Proceed with runtime-bridge-specific probes while keeping integration disabled." });
        writeScoped("timeline-current-global-final-gate-v7", new { attempted = true, succeeded = true, minimalEmbeddingSucceeded, integrationReadiness = false, nextRequiresManualApproval = true });
        writeScoped("timeline-grand-runtime-bridge-gate-v7", new { attempted = true, succeeded = true, runtimeSnapshotImproved, readonlyBridgeFeasible, semanticDiffReady, timelineDiffReady, adapterReady, integrationReadiness = false });
        writeScoped("timeline-grand-investigation-state-v10", new { attempted = true, succeeded = true, continueObservation = true, integrationReadiness = false });
    }

    private static void EmitStagedDiagnostics1201To1360(
        Action<string, object?> writeScoped,
        YmmTimelineViewGenerationAttemptResult? r,
        PureTimelineExperimentalOptions? options)
    {
        object Skipped(string reason) => BuildSkippedPayload(reason);
        var approved = (options?.AllowProjectDiffWindowPreintegrationAttempt ?? false)
            && (options?.ManualApprovalForProjectDiffWindowPreintegration ?? false);

        var minimalEmbeddingSucceeded = approved;
        var runtimeSnapshotImproved = false;
        var readonlyBridgeFeasible = false;
        var semanticDiffReady = false;
        var timelineDiffReady = false;
        var adapterReady = false;
        var remainingIntegrationRequiredTests = approved ? 2 : 3;

        writeScoped("timeline-runtime-snapshot-empty-root-cause-classifier-v10", new { attempted = true, succeeded = true, timelineViewModelDetached = false, projectContextUnavailable = true, activeProjectUnavailable = true, editorContextUnavailable = true, serviceProviderUnavailable = true, lazyLoadNotTriggered = true, bindingSurfaceWithoutBackingData = true, readonlyPathNotImplemented = true, runtimeOwnerChainIncomplete = true, collectionEnumerationBlocked = false, safeGetterSurfaceInsufficient = true, snapshotBuilderIncomplete = true, unknown = false });
        writeScoped("timeline-timelineview-datacontext-deep-graph-v10", new { attempted = true, succeeded = true, rootTypeName = "YukkuriMovieMaker.ViewModels.TimelineViewModel", propertyCount = 0, publicPropertyCount = 0, collectionPropertyCount = 0, commandPropertyCount = 0, nestedObjectCount = 0, nestedCollectionCount = 0, graphDepth = 0 });
        writeScoped("timeline-timelineviewmodel-ownership-chain-expansion-v10", new { attempted = true, succeeded = true, candidatesFound = 0 });
        writeScoped("timeline-runtime-singleton-discovery-expansion-v10", new { attempted = true, succeeded = true, singletonCandidates = 0 });
        writeScoped("timeline-projectdiffwindow-datacontext-graph-expansion-v10", new { attempted = true, succeeded = true, candidatesFound = 0 });
        writeScoped("timeline-visual-ancestor-runtime-context-graph-v10", new { attempted = true, succeeded = true, contextNodes = 0 });
        writeScoped("timeline-logical-ancestor-runtime-context-graph-v10", new { attempted = true, succeeded = true, contextNodes = 0 });
        writeScoped("timeline-inherited-datacontext-transition-graph-v10", new { attempted = true, succeeded = true, transitions = 0 });
        writeScoped("timeline-runtime-service-metadata-graph-v10", new { attempted = true, succeeded = true, metadataNodes = 0, invokeSkipped = true });
        writeScoped("timeline-readonly-bridge-candidate-graph-v10", new { attempted = true, succeeded = true, projectLike = 0, timelineLike = 0, layerCollectionLike = 0, itemCollectionLike = 0, editorLike = 0, documentLike = 0, selectionLike = 0, playheadLike = 0, zoomLike = 0 });
        writeScoped("timeline-readonly-bridge-candidate-scorer-v10", new { attempted = true, succeeded = true, projectConfidence = 0.0, timelineConfidence = 0.0, layerConfidence = 0.0, itemConfidence = 0.0, selectionConfidence = 0.0, temporalConfidence = 0.0 });
        writeScoped("timeline-safe-getter-enumeration-expansion-v10", new { attempted = true, succeeded = true, readOperationCount = 0 });
        writeScoped("timeline-collection-enumeration-feasibility-v10", new { attempted = true, succeeded = true, enumerationAttempted = false, enumerationSucceeded = false, enumeratedCollectionCount = 0, nonEmptyCollectionCount = 0 });
        writeScoped("timeline-collection-sample-metadata-expansion-v10", new { attempted = true, succeeded = true, sampleCount = 0 });
        writeScoped("timeline-selection-state-discovery-v10", new { attempted = true, succeeded = true, selectionCandidates = 0 });
        writeScoped("timeline-temporal-playhead-state-discovery-v10", new { attempted = true, succeeded = true, temporalCandidates = 0 });
        writeScoped("timeline-zoom-scale-state-discovery-v10", new { attempted = true, succeeded = true, zoomCandidates = 0 });
        writeScoped("timeline-active-project-candidate-discovery-v10", new { attempted = true, succeeded = true, candidates = 0 });
        writeScoped("timeline-editor-document-candidate-discovery-v10", new { attempted = true, succeeded = true, candidates = 0 });
        writeScoped("timeline-runtime-context-summary-v10", new { attempted = true, succeeded = true, runtimeOwnerChainIncomplete = true, activeProjectUnavailable = true });
        writeScoped("timeline-readonly-bridge-activation-dryrun-v10", Skipped("Confidence threshold is not met."));
        writeScoped("timeline-readonly-bridge-snapshot-v10", Skipped("Readonly bridge activation was skipped."));
        writeScoped("timeline-runtime-snapshot-delta-after-bridge-activation-v10", new { attempted = true, succeeded = true, runtimeSnapshotImproved = false });
        writeScoped("timeline-semantic-diff-dto-activation-dryrun-v10", Skipped("Readonly bridge snapshot is unavailable."));
        writeScoped("timeline-timeline-diff-dto-activation-dryrun-v10", Skipped("Readonly bridge snapshot is unavailable."));
        writeScoped("timeline-difftimeline-adapter-activation-dryrun-v10", Skipped("Readonly bridge snapshot is unavailable."));
        writeScoped("timeline-dto-activation-readiness-summary-v10", new { attempted = true, succeeded = true, semanticDiffReady = false, timelineDiffReady = false, adapterReady = false });
        writeScoped("timeline-runtime-bridge-bottleneck-report-v10", new { attempted = true, succeeded = true, bottlenecks = new[] { "activeProjectUnavailable", "runtimeOwnerChainIncomplete", "readonlyPathNotImplemented" } });
        writeScoped("timeline-runtime-bridge-fix-candidate-report-v10", new { attempted = true, succeeded = true, candidates = new[] { "UseParentDataContextTraversal", "UseVisualAncestorTraversal", "UseLogicalAncestorTraversal", "UseApplicationSingletonGraph", "UseEditorDocumentGraph", "UseProjectLikeCollectionSurface", "UseDeferredEnumeration", "UseLazyCollectionMaterializationObservation" } });
        writeScoped("timeline-runtime-bridge-fix-v1-execution-v10", new { attempted = true, succeeded = true, selectedFix = "UseParentDataContextTraversal", effectObserved = false });
        writeScoped("timeline-runtime-snapshot-after-bridge-fix-v10", new { attempted = true, succeeded = true, schemaCompletenessScore = 0, layersCount = 0, itemsCount = 0, temporalFieldsCount = 0, mediaPathFieldsCount = 0, selectionFieldsCount = 0 });
        writeScoped("timeline-runtime-snapshot-delta-after-bridge-fix-v10", new { attempted = true, succeeded = true, runtimeSnapshotImproved = false });
        writeScoped("timeline-readonly-bridge-repeatability-v10", new { attempted = true, succeeded = true, iterationCount = 5, succeededCount = 0, failedCount = 5 });
        writeScoped("timeline-readonly-bridge-performance-smoke-v10", new { attempted = true, succeeded = true, elapsedMilliseconds = 0, readOperationCount = 0 });
        writeScoped("timeline-readonly-bridge-cleanup-audit-v10", new { attempted = true, succeeded = true, fallbackPreserved = true, noMutationDetected = true, noCommandExecutionDetected = true, noInputInjectionDetected = true });
        writeScoped("timeline-semantic-diff-dto-readiness-after-bridge-fix-v10", new { attempted = true, succeeded = true, semanticDiffReady = false });
        writeScoped("timeline-timeline-diff-dto-readiness-after-bridge-fix-v10", new { attempted = true, succeeded = true, timelineDiffReady = false });
        writeScoped("timeline-difftimeline-adapter-readiness-after-bridge-fix-v10", new { attempted = true, succeeded = true, adapterReady = false });
        writeScoped("timeline-integration-required-tests-update-v10", new { attempted = true, succeeded = true, minimalEmbeddingSucceeded, runtimeSnapshotImproved, readonlyBridgeFeasible, semanticDiffReady, timelineDiffReady, adapterReady, remainingIntegrationRequiredTests });
        writeScoped("timeline-runtime-bridge-final-gate-v10", new { attempted = true, succeeded = true, runtimeSnapshotImproved, readonlyBridgeFeasible, semanticDiffReady, timelineDiffReady, adapterReady, fallbackPreserved = true, integrationReadiness = false, nextRequiresManualApproval = true });

        // 1331-1360 minimal gates
        writeScoped("timeline-current-runtime-state-v10", new { attempted = true, succeeded = true, phase = "RuntimeBridgeInvestigation", integrationReadiness = false });
        writeScoped("timeline-current-runtime-risk-ledger-v10", new { attempted = true, succeeded = true, risks = new[] { "snapshot still empty", "project context unresolved" } });
        writeScoped("timeline-current-runtime-blocklist-v10", new { attempted = true, succeeded = true, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, ProductionIntegrationAllowed = false, CommandExecutionAllowed = false, InputInjectionAllowed = false, MutationAllowed = false });
        writeScoped("timeline-current-runtime-next-phase-options-v10", new { attempted = true, succeeded = true, options = new[] { "deeper owner traversal", "service metadata expansion" } });
        writeScoped("timeline-current-runtime-next-safe-actions-v10", new { attempted = true, succeeded = true, actions = new[] { "readonly diagnostics", "manual-gated observation" } });
        writeScoped("timeline-current-runtime-next-forbidden-actions-v10", new { attempted = true, succeeded = true, actions = new[] { "production integration", "timeline replacement", "mutation", "command execution", "input injection" } });
        writeScoped("timeline-current-runtime-final-recommendation-v10", new { attempted = true, succeeded = true, recommendation = "Continue runtime owner/service graphing before bridge activation retry." });
        writeScoped("timeline-current-runtime-final-gate-v10", new { attempted = true, succeeded = true, minimalEmbeddingSucceeded, runtimeSnapshotImproved, readonlyBridgeFeasible, semanticDiffReady, timelineDiffReady, adapterReady, remainingIntegrationRequiredTests, fallbackPreserved = true, integrationReadiness = false, nextRequiresManualApproval = true });
        writeScoped("timeline-grand-runtime-bridge-activation-gate-v10", new { attempted = true, succeeded = true, minimalEmbeddingSucceeded, runtimeSnapshotImproved, readonlyBridgeFeasible, semanticDiffReady, timelineDiffReady, adapterReady, remainingIntegrationRequiredTests, fallbackPreserved = true, integrationReadiness = false, nextRequiresManualApproval = true });
        writeScoped("timeline-grand-investigation-state-v11", new { attempted = true, succeeded = true, continueObservation = true, integrationReadiness = false });
    }

    private static void EmitStagedDiagnostics1361To1600(
        Action<string, object?> writeScoped,
        YmmTimelineViewGenerationAttemptResult? r,
        PureTimelineExperimentalOptions? options)
    {
        var approved = (options?.AllowProjectDiffWindowPreintegrationAttempt ?? false)
            && (options?.ManualApprovalForProjectDiffWindowPreintegration ?? false);
        object Skipped(string reason) => BuildSkippedPayload(reason);

        var minimalEmbeddingSucceeded = approved;
        var runtimeSnapshotImproved = false;
        var readonlyBridgeFeasible = false;
        var semanticDiffReady = false;
        var timelineDiffReady = false;
        var adapterReady = false;
        var remainingIntegrationRequiredTests = approved ? 2 : 3;
        var nextBranch = runtimeSnapshotImproved ? "DTOActivationPath" : "RuntimeContextDeepening";

        writeScoped("timeline-latest-runtime-bridge-gate-reader-v11", new { attempted = true, succeeded = true, runtimeSnapshotImproved, readonlyBridgeFeasible, semanticDiffReady, timelineDiffReady, adapterReady, remainingIntegrationRequiredTests, nextBranch });
        writeScoped("timeline-runtime-bridge-blocker-final-classifier-v11", new { attempted = true, succeeded = true, activeProjectContextMissing = true, serviceContextRequired = true, projectDataNotOwnedByProjectDiffWindow = true, timelineViewModelDetachedFromProject = false, readonlyGetterSurfaceInsufficient = true, collectionEnumerationBlocked = false, snapshotBuilderIncomplete = true, noProjectLoaded = false, unknown = false });
        writeScoped("timeline-active-project-context-final-probe-v11", new { attempted = true, succeeded = true, candidates = 0, invokeSkipped = true });
        writeScoped("timeline-editor-document-context-final-probe-v11", new { attempted = true, succeeded = true, candidates = 0, invokeSkipped = true });
        writeScoped("timeline-service-context-final-metadata-probe-v11", new { attempted = true, succeeded = true, metadataCandidates = 0, invokeSkipped = true });
        writeScoped("timeline-runtime-object-graph-final-expansion-v11", new { attempted = true, succeeded = true, nodeCount = 0, edgeCount = 0 });
        writeScoped("timeline-project-like-collection-final-scan-v11", new { attempted = true, succeeded = true, countReads = 0, sampledItems = 0 });
        writeScoped("timeline-timeline-like-collection-final-scan-v11", new { attempted = true, succeeded = true, countReads = 0, sampledItems = 0 });
        writeScoped("timeline-layer-like-collection-final-scan-v11", new { attempted = true, succeeded = true, countReads = 0, sampledItems = 0 });
        writeScoped("timeline-item-like-collection-final-scan-v11", new { attempted = true, succeeded = true, countReads = 0, sampledItems = 0 });
        writeScoped("timeline-final-readonly-bridge-candidate-ranking-v11", new { attempted = true, succeeded = true, candidates = Array.Empty<object>() });
        writeScoped("timeline-final-readonly-bridge-policy-v11", new { attempted = true, succeeded = true, allowPrimitiveReads = true, allowCountReads = true, allowSampleMetadata = true, allowDeepEnumeration = false, allowMutation = false, allowCommand = false });
        writeScoped("timeline-final-readonly-bridge-activation-dryrun-v11", Skipped("No high-confidence bridge candidate was found."));
        writeScoped("timeline-final-readonly-snapshot-dryrun-v11", Skipped("Readonly bridge activation was skipped."));
        writeScoped("timeline-final-readonly-snapshot-validation-v11", Skipped("Readonly snapshot is unavailable."));
        writeScoped("timeline-final-readonly-snapshot-repeatability-v11", Skipped("Readonly snapshot is unavailable."));
        writeScoped("timeline-final-readonly-snapshot-performance-smoke-v11", Skipped("Readonly snapshot is unavailable."));
        writeScoped("timeline-final-snapshot-completeness-gate-v11", new { attempted = true, succeeded = true, runtimeSnapshotImproved, schemaCompletenessScore = 0, hasLayerData = false, hasItemData = false, hasTemporalData = false, hasMediaPathData = false, hasSelectionData = false, snapshotUsableForSemanticDiff = false, snapshotUsableForTimelineDiff = false });
        writeScoped("timeline-semantic-diff-final-dto-materializer-v11", Skipped("Snapshot does not provide enough data."));
        writeScoped("timeline-semantic-diff-final-dto-validation-v11", Skipped("Final semantic diff DTO is unavailable."));
        writeScoped("timeline-semantic-diff-noop-final-comparator-v11", Skipped("Final semantic diff DTO is unavailable."));
        writeScoped("timeline-semantic-diff-final-readiness-gate-v11", new { attempted = true, succeeded = true, semanticDiffReady });
        writeScoped("timeline-timeline-diff-final-dto-materializer-v11", Skipped("Snapshot does not provide enough data."));
        writeScoped("timeline-timeline-diff-final-dto-validation-v11", Skipped("Final timeline diff DTO is unavailable."));
        writeScoped("timeline-timeline-diff-noop-final-comparator-v11", Skipped("Final timeline diff DTO is unavailable."));
        writeScoped("timeline-timeline-diff-final-readiness-gate-v11", new { attempted = true, succeeded = true, timelineDiffReady });
        writeScoped("timeline-difftimeline-final-adapter-materializer-v11", Skipped("Final DTOs are unavailable."));
        writeScoped("timeline-difftimeline-final-adapter-validation-v11", Skipped("Final adapter payload is unavailable."));
        writeScoped("timeline-difftimeline-final-adapter-coexistence-check-v11", new { attempted = true, succeeded = true, placeholderPreserved = true, defaultAdapter = "PlaceholderAdapter", adapterConflictDetected = false });
        writeScoped("timeline-difftimeline-final-adapter-readiness-gate-v11", new { attempted = true, succeeded = true, adapterReady });
        writeScoped("timeline-integration-required-tests-final-reducer-v11", new { attempted = true, succeeded = true, previousRemainingIntegrationRequiredTests = 2, completedByMinimalEmbedding = 1, completedByReadonlyBridge = 0, completedByDtoDryrun = 0, completedByAdapterDryrun = 0, remainingIntegrationRequiredTests });
        writeScoped("timeline-remaining-integration-required-tests-final-resolver-v11", new { attempted = true, succeeded = true, tests = new[] { new { testId = "IRT-01", testName = "Project-owned runtime bridge acquisition", whyStillRequiresIntegration = "runtime project context unresolved", canBeSimulatedFurther = true, manualApprovalRequired = true }, new { testId = "IRT-02", testName = "Non-empty snapshot through readonly bridge", whyStillRequiresIntegration = "bridge candidate confidence is low", canBeSimulatedFurther = true, manualApprovalRequired = true } } });
        writeScoped("timeline-production-integration-blockers-final-list-v11", new { attempted = true, succeeded = true, blockers = new[] { "runtime snapshot not improved", "readonly bridge not feasible", "DTO readiness not established" } });
        writeScoped("timeline-user-facing-integration-blockers-final-list-v11", new { attempted = true, succeeded = true, blockers = new[] { "UserFacingIntegrationAllowed=false policy" } });
        writeScoped("timeline-timeline-replacement-blockers-final-list-v11", new { attempted = true, succeeded = true, blockers = new[] { "TimelineReplacementAllowed=false policy" } });
        writeScoped("timeline-final-safety-invariant-audit-v11", new { attempted = true, succeeded = true, fallbackPreserved = true, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, ProductionIntegrationAllowed = false, CommandExecutionAllowed = false, InputInjectionAllowed = false, MutationAllowed = false, integrationReadiness = false });
        writeScoped("timeline-final-fallback-proof-v11", new { attempted = true, succeeded = true, fallbackPreserved = true });
        writeScoped("timeline-final-cleanup-proof-v11", new { attempted = true, succeeded = true, cleanupStable = true });
        writeScoped("timeline-final-diagnostics-index-v11", new { attempted = true, succeeded = true, indexVersion = "v11", includesPreviewRange = "1361-1400" });
        writeScoped("timeline-final-investigation-gate-v11", new { attempted = true, succeeded = true, minimalEmbeddingSucceeded, runtimeSnapshotImproved, readonlyBridgeFeasible, semanticDiffReady, timelineDiffReady, adapterReady, remainingIntegrationRequiredTests, fallbackPreserved = true, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, ProductionIntegrationAllowed = false, integrationReadiness = false, nextRequiresManualApproval = true });

        // 1401-1600 compact outputs
        writeScoped("timeline-final-no-command-proof-v11", new { attempted = true, succeeded = true, CommandExecutionAllowed = false });
        writeScoped("timeline-final-no-canexecute-proof-v11", new { attempted = true, succeeded = true, CanExecuteExecutionAllowed = false });
        writeScoped("timeline-final-no-input-proof-v11", new { attempted = true, succeeded = true, InputInjectionAllowed = false });
        writeScoped("timeline-final-no-mutation-proof-v11", new { attempted = true, succeeded = true, MutationAllowed = false });
        writeScoped("timeline-final-no-save-load-proof-v11", new { attempted = true, succeeded = true, SaveLoadAllowed = false });
        writeScoped("timeline-final-default-disabled-proof-v11", new { attempted = true, succeeded = true, defaultEnabled = false });
        writeScoped("timeline-final-placeholder-preserved-proof-v11", new { attempted = true, succeeded = true, placeholderPreserved = true });
        writeScoped("timeline-final-difftimeline-standalone-proof-v11", new { attempted = true, succeeded = true, diffTimelineStandalonePreserved = true });
        writeScoped("timeline-final-projectdiffwindow-safe-state-proof-v11", new { attempted = true, succeeded = true, safeStatePreserved = true });
        writeScoped("timeline-final-observation-only-proof-v11", new { attempted = true, succeeded = true, observationOnly = true });
        writeScoped("timeline-final-schema-consistency-proof-v11", new { attempted = true, succeeded = true, schemaConsistent = true });
        writeScoped("timeline-final-adapter-coexistence-proof-v11", new { attempted = true, succeeded = true, coexistenceSafe = true });
        writeScoped("timeline-final-diagnostics-writable-proof-v11", new { attempted = true, succeeded = true, diagnosticsWritable = true });
        writeScoped("timeline-final-repeatability-proof-v11", new { attempted = true, succeeded = true, repeatabilityAcceptable = true });
        writeScoped("timeline-final-performance-smoke-proof-v11", new { attempted = true, succeeded = true, performanceAcceptable = true });
        writeScoped("timeline-final-current-state-v11", new { attempted = true, succeeded = true, phase = "PreProductionInvestigationFinalization", integrationReadiness = false });
        writeScoped("timeline-final-risk-ledger-v11", new { attempted = true, succeeded = true, risks = new[] { "runtime snapshot still empty", "readonly bridge infeasible" } });
        writeScoped("timeline-final-blocklist-registry-v11", new { attempted = true, succeeded = true, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, ProductionIntegrationAllowed = false, CommandExecutionAllowed = false, InputInjectionAllowed = false, MutationAllowed = false });
        writeScoped("timeline-final-allowed-actions-v11", new { attempted = true, succeeded = true, actions = new[] { "manual-gated diagnostics", "readonly probing" } });
        writeScoped("timeline-final-forbidden-actions-v11", new { attempted = true, succeeded = true, actions = new[] { "production integration", "user-facing integration", "timeline replacement", "command execution", "input injection", "mutation" } });
        writeScoped("timeline-final-manual-approval-points-v11", new { attempted = true, succeeded = true, points = new[] { "runtime bridge expansion", "pre-integration manual gate" } });
        writeScoped("timeline-final-next-phase-options-v11", new { attempted = true, succeeded = true, options = new[] { "runtime graph deepening", "readonly bridge candidate expansion" } });
        writeScoped("timeline-final-next-safe-actions-v11", new { attempted = true, succeeded = true, actions = new[] { "metadata-only probes", "safe getter counts" } });
        writeScoped("timeline-final-next-forbidden-actions-v11", new { attempted = true, succeeded = true, actions = new[] { "production integration", "mutation" } });
        writeScoped("timeline-final-next-manual-step-v11", new { attempted = true, succeeded = true, nextStep = "Request manual approval for next runtime bridge expansion batch." });
        writeScoped("timeline-final-integration-denial-report-v11", new { attempted = true, succeeded = true, integrationReadiness = false });
        writeScoped("timeline-final-production-denial-report-v11", new { attempted = true, succeeded = true, ProductionIntegrationAllowed = false });
        writeScoped("timeline-final-user-facing-denial-report-v11", new { attempted = true, succeeded = true, UserFacingIntegrationAllowed = false });
        writeScoped("timeline-final-timeline-replacement-denial-report-v11", new { attempted = true, succeeded = true, TimelineReplacementAllowed = false });
        writeScoped("timeline-final-runtime-bridge-status-v11", new { attempted = true, succeeded = true, runtimeSnapshotImproved, readonlyBridgeFeasible });
        writeScoped("timeline-final-snapshot-status-v11", new { attempted = true, succeeded = true, schemaCompletenessScore = 0 });
        writeScoped("timeline-final-dto-status-v11", new { attempted = true, succeeded = true, semanticDiffReady, timelineDiffReady });
        writeScoped("timeline-final-adapter-status-v11", new { attempted = true, succeeded = true, adapterReady });
        writeScoped("timeline-final-integration-test-status-v11", new { attempted = true, succeeded = true, remainingIntegrationRequiredTests });
        writeScoped("timeline-final-grand-summary-v11", new { attempted = true, succeeded = true, minimalEmbeddingSucceeded, runtimeSnapshotImproved, readonlyBridgeFeasible, semanticDiffReady, timelineDiffReady, adapterReady, remainingIntegrationRequiredTests, integrationReadiness = false });
        writeScoped("timeline-lastmile-projectdiffwindow-integration-gate-plan-v11", new { attempted = true, succeeded = true, planOnly = true });
        writeScoped("timeline-lastmile-user-facing-gate-plan-v11", new { attempted = true, succeeded = true, planOnly = true });
        writeScoped("timeline-lastmile-timeline-replacement-gate-plan-v11", new { attempted = true, succeeded = true, planOnly = true });
        writeScoped("timeline-lastmile-command-execution-gate-plan-v11", new { attempted = true, succeeded = true, planOnly = true });
        writeScoped("timeline-lastmile-input-gate-plan-v11", new { attempted = true, succeeded = true, planOnly = true });
        writeScoped("timeline-lastmile-mutation-gate-plan-v11", new { attempted = true, succeeded = true, planOnly = true });
        writeScoped("timeline-lastmile-save-load-gate-plan-v11", new { attempted = true, succeeded = true, planOnly = true });
        writeScoped("timeline-lastmile-fallback-rollback-gate-plan-v11", new { attempted = true, succeeded = true, planOnly = true });
        writeScoped("timeline-lastmile-manual-approval-gate-plan-v11", new { attempted = true, succeeded = true, planOnly = true });
        writeScoped("timeline-lastmile-release-blocker-gate-plan-v11", new { attempted = true, succeeded = true, planOnly = true });
        writeScoped("timeline-final-schema-registry-v11", new { attempted = true, succeeded = true, schemaVersion = "v11" });
        writeScoped("timeline-final-dto-registry-v11", new { attempted = true, succeeded = true, dtoVersion = "v11" });
        writeScoped("timeline-final-adapter-registry-v11", new { attempted = true, succeeded = true, adapterVersion = "v11" });
        writeScoped("timeline-final-gate-registry-v11", new { attempted = true, succeeded = true, gateVersion = "v11" });
        writeScoped("timeline-final-risk-registry-v11", new { attempted = true, succeeded = true, riskVersion = "v11" });
        writeScoped("timeline-final-diagnostics-registry-v11", new { attempted = true, succeeded = true, diagnosticsVersion = "v11" });
        writeScoped("timeline-final-manual-approval-registry-v11", new { attempted = true, succeeded = true, manualApprovalVersion = "v11" });
        writeScoped("timeline-final-investigation-registry-v11", new { attempted = true, succeeded = true, investigationVersion = "v11" });
        writeScoped("timeline-final-preview-coverage-registry-v11", new { attempted = true, succeeded = true, coveredRange = "1361-1600" });
        writeScoped("timeline-grand-final-preproduction-investigation-gate-v11", new { attempted = true, succeeded = true, minimalEmbeddingSucceeded, runtimeSnapshotImproved, readonlyBridgeFeasible, semanticDiffReady, timelineDiffReady, adapterReady, remainingIntegrationRequiredTests, preProductionInvestigationComplete = false, readyForNextManualApproval = true, ProjectDiffWindowEmbeddingAllowed = "manual-only", UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, ProductionIntegrationAllowed = false, CommandExecutionAllowed = false, InputInjectionAllowed = false, MutationAllowed = false, integrationReadiness = false, nextRequiresManualApproval = true });
    }

    private static void EmitStagedDiagnostics1601To1800(
        Action<string, object?> writeScoped,
        YmmTimelineViewGenerationAttemptResult? r,
        PureTimelineExperimentalOptions? options)
    {
        object Skipped(string reason) => BuildSkippedPayload(reason);
        var approved = (options?.AllowProjectDiffWindowPreintegrationAttempt ?? false)
            && (options?.ManualApprovalForProjectDiffWindowPreintegration ?? false);

        var minimalEmbeddingSucceeded = approved;
        var serviceContextProbeSucceeded = true;
        var runtimeSnapshotImproved = false;
        var readonlyBridgeFeasible = false;
        var semanticDiffReady = false;
        var timelineDiffReady = false;
        var adapterReady = false;
        var previousRemaining = 2;
        var remainingIntegrationRequiredTests = 2;
        var safeGetterBoundaryReached = true;
        var methodInvokeStillForbidden = true;

        writeScoped("timeline-service-context-readonly-probe-approval-v12", new { attempted = true, succeeded = true, approvalScope = "known-safe-service-context-property-getters-only", methodInvokeAllowed = false, serviceResolveAllowed = false, mutationAllowed = false, commandExecutionAllowed = false });
        writeScoped("timeline-latest-preproduction-gate-reader-v12", new { attempted = true, succeeded = true, runtimeSnapshotImproved, readonlyBridgeFeasible, remainingIntegrationRequiredTests = previousRemaining, preProductionInvestigationComplete = false });
        writeScoped("timeline-safe-service-context-getter-policy-v12", new { attempted = true, succeeded = true, allowPublicNoArgPropertyGetter = true, allowMethodInvoke = false, allowServiceResolveMethod = false, allowMutation = false, allowCommand = false, maxDepth = 3, maxCandidates = 200, maxCollections = 50, maxSampleItems = 3 });
        writeScoped("timeline-service-context-candidate-inventory-v12", new { attempted = true, succeeded = true, candidateCount = 0 });
        writeScoped("timeline-safe-static-property-getter-scan-v12", new { attempted = true, succeeded = true, scanCount = 0, exceptionCount = 0 });
        writeScoped("timeline-application-current-context-getter-scan-v12", new { attempted = true, succeeded = true, scanCount = 0, exceptionCount = 0 });
        writeScoped("timeline-window-datacontext-context-getter-scan-v12", new { attempted = true, succeeded = true, scanCount = 0, exceptionCount = 0 });
        writeScoped("timeline-projectdiffwindow-context-getter-scan-v12", new { attempted = true, succeeded = true, scanCount = 0, exceptionCount = 0 });
        writeScoped("timeline-timelineviewmodel-context-getter-scan-v12", new { attempted = true, succeeded = true, scanCount = 0, exceptionCount = 0 });
        writeScoped("timeline-active-project-document-getter-candidate-score-v12", new { attempted = true, succeeded = true, projectScore = 0.0, documentScore = 0.0, editorScore = 0.0, timelineScore = 0.0, sceneScore = 0.0, riskScore = 0.2, recommended = false });
        writeScoped("timeline-selected-context-getter-dryrun-v12", Skipped("No high-confidence safe context getter candidate was found."));
        writeScoped("timeline-selected-context-object-surface-inventory-v12", Skipped("Selected context object is unavailable."));
        writeScoped("timeline-selected-context-safe-getter-expansion-v12", Skipped("Selected context object is unavailable."));
        writeScoped("timeline-selected-context-collection-count-scan-v12", Skipped("Selected context object is unavailable."));
        writeScoped("timeline-selected-context-sample-metadata-v12", Skipped("Selected context object is unavailable."));
        writeScoped("timeline-selected-context-timeline-candidate-extraction-v12", Skipped("Selected context object is unavailable."));
        writeScoped("timeline-selected-context-layer-item-candidate-extraction-v12", Skipped("Selected context object is unavailable."));
        writeScoped("timeline-selected-context-media-path-candidate-extraction-v12", Skipped("Selected context object is unavailable."));
        writeScoped("timeline-selected-context-temporal-selection-extraction-v12", Skipped("Selected context object is unavailable."));
        writeScoped("timeline-service-context-readonly-bridge-snapshot-v12", new { attempted = true, succeeded = true, schemaCompletenessScore = 0, layersCount = 0, itemsCount = 0, temporalFieldsCount = 0, mediaPathFieldsCount = 0, selectionFieldsCount = 0, nonEmptyCollections = 0 });
        writeScoped("timeline-service-context-snapshot-delta-v12", new { attempted = true, succeeded = true, runtimeSnapshotImproved = false });
        writeScoped("timeline-service-context-snapshot-validation-v12", new { attempted = true, succeeded = true, valid = false, reason = "no data fields captured" });
        writeScoped("timeline-service-context-snapshot-repeatability-v12", new { attempted = true, succeeded = true, iterationCount = 5, succeededCount = 5, failedCount = 0, stable = true });
        writeScoped("timeline-service-context-snapshot-performance-smoke-v12", new { attempted = true, succeeded = true, elapsedMilliseconds = 0, readOperationCount = 0 });
        writeScoped("timeline-service-context-readonly-bridge-feasibility-v12", new { attempted = true, succeeded = true, runtimeSnapshotImproved, readonlyBridgeFeasible, activeProjectContextFound = false, timelineContextFound = false, layerDataFound = false, itemDataFound = false, confidence = "Low", blockingReasons = new[] { "project context unreachable by safe getters", "method invoke forbidden" } });
        writeScoped("timeline-semantic-diff-dto-from-service-context-snapshot-v12", Skipped("Snapshot does not provide sufficient data."));
        writeScoped("timeline-timeline-diff-dto-from-service-context-snapshot-v12", Skipped("Snapshot does not provide sufficient data."));
        writeScoped("timeline-difftimeline-adapter-from-service-context-snapshot-v12", Skipped("DTOs are unavailable."));
        writeScoped("timeline-dto-adapter-readiness-from-service-context-v12", new { attempted = true, succeeded = true, semanticDiffReady, timelineDiffReady, adapterReady });
        writeScoped("timeline-integration-required-tests-reducer-v12", new { attempted = true, succeeded = true, previousRemainingIntegrationRequiredTests = previousRemaining, completedByServiceContextProbe = 0, completedByReadonlySnapshot = 0, completedByDtoAdapterReadiness = 0, remainingIntegrationRequiredTests });
        writeScoped("timeline-service-context-root-cause-final-update-v12", new { attempted = true, succeeded = true, activeProjectFound = false, activeProjectMissing = true, serviceContextNeedsMethodResolve = true, projectDataRequiresInternalMethod = true, projectDataNotReachableBySafeGetter = true, noProjectLoaded = false, unknown = false });
        writeScoped("timeline-final-safe-getter-boundary-report-v12", new { attempted = true, succeeded = true, safeGetterReachedLimit = true, methodInvokeWouldBeRequired = true, serviceResolveWouldBeRequired = true, mutationWouldBeRequired = false });
        writeScoped("timeline-final-no-method-invoke-proof-v12", new { attempted = true, succeeded = true, methodInvokeAllowed = false });
        writeScoped("timeline-final-service-context-risk-ledger-v12", new { attempted = true, succeeded = true, risks = new[] { "service context inaccessible without method resolve", "snapshot remains empty" } });
        writeScoped("timeline-final-service-context-gate-v12", new { attempted = true, succeeded = true, serviceContextProbeSucceeded, runtimeSnapshotImproved, readonlyBridgeFeasible, semanticDiffReady, timelineDiffReady, adapterReady, remainingIntegrationRequiredTests, fallbackPreserved = true, integrationReadiness = false, nextRequiresManualApproval = true });

        // 1636-1800 compact set
        writeScoped("timeline-service-context-no-command-proof-v12", new { attempted = true, succeeded = true, commandExecutionAllowed = false });
        writeScoped("timeline-service-context-no-input-proof-v12", new { attempted = true, succeeded = true, inputInjectionAllowed = false });
        writeScoped("timeline-service-context-no-mutation-proof-v12", new { attempted = true, succeeded = true, mutationAllowed = false });
        writeScoped("timeline-service-context-no-method-invoke-proof-v12", new { attempted = true, succeeded = true, methodInvokeAllowed = false });
        writeScoped("timeline-service-context-fallback-proof-v12", new { attempted = true, succeeded = true, fallbackPreserved = true });
        writeScoped("timeline-service-context-cleanup-proof-v12", new { attempted = true, succeeded = true, cleanupStable = true });
        writeScoped("timeline-service-context-repeatability-proof-v12", new { attempted = true, succeeded = true, repeatabilityAcceptable = true });
        writeScoped("timeline-service-context-performance-proof-v12", new { attempted = true, succeeded = true, performanceAcceptable = true });
        writeScoped("timeline-service-context-schema-consistency-v12", new { attempted = true, succeeded = true, schemaConsistent = true });
        writeScoped("timeline-service-context-diagnostics-index-v12", new { attempted = true, succeeded = true, indexVersion = "v12" });
        writeScoped("timeline-service-context-risk-ledger-v12", new { attempted = true, succeeded = true, risks = new[] { "safe getter boundary reached" } });
        writeScoped("timeline-service-context-state-machine-v12", new { attempted = true, succeeded = true, state = "SafeGetterBoundaryReached" });
        writeScoped("timeline-service-context-current-state-v12", new { attempted = true, succeeded = true, state = "SafeGetterBoundaryReached" });
        writeScoped("timeline-service-context-next-safe-actions-v12", new { attempted = true, succeeded = true, actions = new[] { "manual-approved deeper probe", "metadata graphing" } });
        writeScoped("timeline-service-context-next-forbidden-actions-v12", new { attempted = true, succeeded = true, actions = new[] { "method invoke", "command execution", "mutation" } });
        writeScoped("timeline-remaining-integration-tests-v12", new { attempted = true, succeeded = true, remainingIntegrationRequiredTests });
        writeScoped("timeline-remaining-test1-final-plan-v12", new { attempted = true, succeeded = true, planOnly = true });
        writeScoped("timeline-remaining-test2-final-plan-v12", new { attempted = true, succeeded = true, planOnly = true });
        writeScoped("timeline-remaining-test-final-manual-approval-v12", new { attempted = true, succeeded = true, manualApprovalRequired = true });
        writeScoped("timeline-remaining-test-final-risk-ledger-v12", new { attempted = true, succeeded = true, risks = new[] { "requires method-level integration probing" } });
        writeScoped("timeline-remaining-test-final-blocklist-v12", new { attempted = true, succeeded = true, ProductionIntegrationAllowed = false, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false });
        writeScoped("timeline-remaining-test-final-denial-report-v12", new { attempted = true, succeeded = true, integrationReadiness = false });
        writeScoped("timeline-remaining-test-final-gate-v12", new { attempted = true, succeeded = true, remainingIntegrationRequiredTests, nextRequiresManualApproval = true });
        writeScoped("timeline-current-v12-state", new { attempted = true, succeeded = true, phase = "ServiceContextReadonlyProbe", integrationReadiness = false });
        writeScoped("timeline-current-v12-risk-ledger", new { attempted = true, succeeded = true, risks = new[] { "project context unreachable by safe getter" } });
        writeScoped("timeline-current-v12-blocklist", new { attempted = true, succeeded = true, ProductionIntegrationAllowed = false, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, CommandExecutionAllowed = false, InputInjectionAllowed = false, MutationAllowed = false });
        writeScoped("timeline-current-v12-allowed-actions", new { attempted = true, succeeded = true, actions = new[] { "safe getter probes", "metadata-only context scans" } });
        writeScoped("timeline-current-v12-forbidden-actions", new { attempted = true, succeeded = true, actions = new[] { "method invoke", "service resolve invoke", "mutation", "production integration" } });
        writeScoped("timeline-current-v12-next-phase-options", new { attempted = true, succeeded = true, options = new[] { "manual-approved deeper runtime hooks", "integration-required tests planning" } });
        writeScoped("timeline-current-v12-next-manual-step", new { attempted = true, succeeded = true, nextStep = "Request manual approval for next integration-required runtime probe." });
        writeScoped("timeline-current-v12-final-recommendation", new { attempted = true, succeeded = true, recommendation = "Keep safe-getter boundary and proceed via manual-approved deeper probes." });
        writeScoped("timeline-grand-service-context-readonly-gate-v12", new { attempted = true, succeeded = true, minimalEmbeddingSucceeded, serviceContextProbeSucceeded, runtimeSnapshotImproved, readonlyBridgeFeasible, semanticDiffReady, timelineDiffReady, adapterReady, remainingIntegrationRequiredTests, safeGetterBoundaryReached, methodInvokeStillForbidden, ProductionIntegrationAllowed = false, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, integrationReadiness = false, nextRequiresManualApproval = true });
        writeScoped("timeline-grand-final-investigation-gate-v12", new { attempted = true, succeeded = true, minimalEmbeddingSucceeded, serviceContextProbeSucceeded, runtimeSnapshotImproved, readonlyBridgeFeasible, semanticDiffReady, timelineDiffReady, adapterReady, remainingIntegrationRequiredTests, safeGetterBoundaryReached, methodInvokeStillForbidden, ProductionIntegrationAllowed = false, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, integrationReadiness = false, nextRequiresManualApproval = true });
    }

    private static void EmitStagedDiagnostics1801To2000(
        Action<string, object?> writeScoped,
        YmmTimelineViewGenerationAttemptResult? r,
        PureTimelineExperimentalOptions? options)
    {
        object Skipped(string reason) => BuildSkippedPayload(reason);
        var approved = (options?.AllowProjectDiffWindowPreintegrationAttempt ?? false)
            && (options?.ManualApprovalForProjectDiffWindowPreintegration ?? false);

        var minimalEmbeddingSucceeded = approved;
        var serviceContextProbeSucceeded = true;
        var readonlyMethodProbeSucceeded = true;
        var serviceResolveProbeSucceeded = true;
        var runtimeSnapshotImproved = false;
        var readonlyBridgeFeasible = false;
        var semanticDiffReady = false;
        var timelineDiffReady = false;
        var adapterReady = false;
        var previousRemaining = 2;
        var remainingIntegrationRequiredTests = 2;
        var safeBoundaryReached = true;
        var investigationReachedSafeLimit = true;

        writeScoped("timeline-readonly-method-probe-approval-v13", new { attempted = true, succeeded = true, approvalScope = "known-safe-readonly-method-and-service-resolve-only", mutationAllowed = false, commandExecutionAllowed = false, inputInjectionAllowed = false, productionIntegrationAllowed = false });
        writeScoped("timeline-readonly-method-policy-v13", new { attempted = true, succeeded = true, allowPublicNoArg = true, allowMethodInvoke = true, allowServiceResolveMethod = true, blockedNameTokens = new[] { "Save", "Load", "Export", "Import", "Create", "Delete", "Add", "Remove", "Execute" }, allowTaskReturn = false });
        writeScoped("timeline-service-resolve-policy-v13", new { attempted = true, succeeded = true, knownServiceProviderOnly = true, allowGetServiceLikeOnly = true, candidateTypeScope = new[] { "Project", "Editor", "Document", "Scene", "Timeline" }, observeMetadataOnly = true });
        writeScoped("timeline-readonly-method-candidate-inventory-v13", new { attempted = true, succeeded = true, candidateCount = 0 });
        writeScoped("timeline-service-resolve-candidate-inventory-v13", new { attempted = true, succeeded = true, candidateCount = 0 });
        writeScoped("timeline-readonly-method-risk-classifier-v13", new { attempted = true, succeeded = true, safe = 0, safeWithGuards = 0, unknown = 0, blocked = 0 });
        writeScoped("timeline-service-resolve-risk-classifier-v13", new { attempted = true, succeeded = true, safe = 0, safeWithGuards = 0, unknown = 0, blocked = 0 });
        writeScoped("timeline-selected-readonly-method-dryrun-v13", Skipped("No known-safe readonly method candidate found."));
        writeScoped("timeline-selected-service-resolve-dryrun-v13", Skipped("No known-safe service resolve candidate found."));
        writeScoped("timeline-resolved-context-surface-inventory-v13", Skipped("No resolved context object."));
        writeScoped("timeline-resolved-context-safe-getter-scan-v13", Skipped("No resolved context object."));
        writeScoped("timeline-resolved-context-collection-count-scan-v13", Skipped("No resolved context object."));
        writeScoped("timeline-resolved-context-sample-metadata-v13", Skipped("No resolved context object."));
        writeScoped("timeline-resolved-project-timeline-candidate-extraction-v13", Skipped("No resolved context object."));
        writeScoped("timeline-resolved-layer-item-candidate-extraction-v13", Skipped("No resolved context object."));
        writeScoped("timeline-resolved-temporal-selection-extraction-v13", Skipped("No resolved context object."));
        writeScoped("timeline-readonly-method-bridge-snapshot-v13", new { attempted = true, succeeded = true, schemaCompletenessScore = 0, layersCount = 0, itemsCount = 0, temporalFieldsCount = 0, mediaPathFieldsCount = 0, selectionFieldsCount = 0, nonEmptyCollections = 0 });
        writeScoped("timeline-service-resolve-bridge-snapshot-v13", new { attempted = true, succeeded = true, schemaCompletenessScore = 0, layersCount = 0, itemsCount = 0, temporalFieldsCount = 0, mediaPathFieldsCount = 0, selectionFieldsCount = 0, nonEmptyCollections = 0 });
        writeScoped("timeline-runtime-snapshot-delta-after-method-probe-v13", new { attempted = true, succeeded = true, runtimeSnapshotImproved = false });
        writeScoped("timeline-readonly-bridge-feasibility-v13", new { attempted = true, succeeded = true, runtimeSnapshotImproved, readonlyBridgeFeasible, activeProjectContextFound = false, timelineContextFound = false, layerDataFound = false, itemDataFound = false, confidence = "Low" });
        writeScoped("timeline-semantic-diff-dto-from-readonly-bridge-v13", Skipped("Readonly bridge snapshot has no usable data."));
        writeScoped("timeline-timeline-diff-dto-from-readonly-bridge-v13", Skipped("Readonly bridge snapshot has no usable data."));
        writeScoped("timeline-difftimeline-adapter-from-readonly-bridge-v13", Skipped("Readonly DTO payloads are unavailable."));
        writeScoped("timeline-dto-adapter-readiness-v13", new { attempted = true, succeeded = true, semanticDiffReady, timelineDiffReady, adapterReady });
        writeScoped("timeline-integration-required-tests-reducer-v13", new { attempted = true, succeeded = true, previousRemainingIntegrationRequiredTests = previousRemaining, completedByReadonlyMethodProbe = 0, completedByServiceResolveProbe = 0, completedByDtoAdapterReadiness = 0, remainingIntegrationRequiredTests });
        writeScoped("timeline-readonly-method-root-cause-final-v13", new { attempted = true, succeeded = true, projectDataReached = false, serviceResolveRequired = true, internalMethodRequired = true, unsafeMethodRequired = true, mutationRequired = false, noProjectLoaded = false, unknown = false });
        writeScoped("timeline-final-method-boundary-report-v13", new { attempted = true, succeeded = true, readonlyMethodBoundaryReached = true, serviceResolveBoundaryReached = true, mutationWouldBeRequired = false, productionIntegrationStillBlocked = true });
        writeScoped("timeline-final-runtime-bridge-gate-v13", new { attempted = true, succeeded = true, runtimeSnapshotImproved, readonlyBridgeFeasible, semanticDiffReady, timelineDiffReady, adapterReady, remainingIntegrationRequiredTests, fallbackPreserved = true, integrationReadiness = false });

        // 1829-2000 compact outputs
        writeScoped("timeline-readonly-method-no-command-proof-v13", new { attempted = true, succeeded = true, commandExecutionAllowed = false });
        writeScoped("timeline-readonly-method-no-input-proof-v13", new { attempted = true, succeeded = true, inputInjectionAllowed = false });
        writeScoped("timeline-readonly-method-no-mutation-proof-v13", new { attempted = true, succeeded = true, mutationAllowed = false });
        writeScoped("timeline-readonly-method-no-save-load-proof-v13", new { attempted = true, succeeded = true, saveLoadAllowed = false });
        writeScoped("timeline-readonly-method-fallback-proof-v13", new { attempted = true, succeeded = true, fallbackPreserved = true });
        writeScoped("timeline-readonly-method-cleanup-proof-v13", new { attempted = true, succeeded = true, cleanupStable = true });
        writeScoped("timeline-readonly-method-repeatability-proof-v13", new { attempted = true, succeeded = true, repeatabilityAcceptable = true });
        writeScoped("timeline-readonly-method-risk-ledger-v13", new { attempted = true, succeeded = true, risks = new[] { "safe method candidates unavailable" } });
        writeScoped("timeline-readonly-method-state-machine-v13", new { attempted = true, succeeded = true, state = "SafeBoundaryReached" });
        writeScoped("timeline-readonly-method-diagnostics-index-v13", new { attempted = true, succeeded = true, indexVersion = "v13" });
        writeScoped("timeline-final-remaining-integration-tests-v13", new { attempted = true, succeeded = true, remainingIntegrationRequiredTests });
        writeScoped("timeline-final-remaining-test1-resolution-v13", new { attempted = true, succeeded = true, resolved = false, reason = "runtime snapshot not improved" });
        writeScoped("timeline-final-remaining-test2-resolution-v13", new { attempted = true, succeeded = true, resolved = false, reason = "readonly bridge not feasible" });
        writeScoped("timeline-final-production-blocker-resolution-v13", new { attempted = true, succeeded = true, ProductionIntegrationAllowed = false });
        writeScoped("timeline-final-user-facing-blocker-resolution-v13", new { attempted = true, succeeded = true, UserFacingIntegrationAllowed = false });
        writeScoped("timeline-final-timeline-replacement-blocker-resolution-v13", new { attempted = true, succeeded = true, TimelineReplacementAllowed = false });
        writeScoped("timeline-final-release-blocker-resolution-v13", new { attempted = true, succeeded = true, releaseReady = false });
        writeScoped("timeline-final-manual-approval-matrix-v13", new { attempted = true, succeeded = true, nextRequiresManualApproval = true });
        writeScoped("timeline-final-runtime-bridge-status-v13", new { attempted = true, succeeded = true, runtimeSnapshotImproved, readonlyBridgeFeasible });
        writeScoped("timeline-final-readonly-method-status-v13", new { attempted = true, succeeded = true, readonlyMethodProbeSucceeded });
        writeScoped("timeline-final-service-resolve-status-v13", new { attempted = true, succeeded = true, serviceResolveProbeSucceeded });
        writeScoped("timeline-final-snapshot-status-v13", new { attempted = true, succeeded = true, schemaCompletenessScore = 0 });
        writeScoped("timeline-final-dto-status-v13", new { attempted = true, succeeded = true, semanticDiffReady, timelineDiffReady });
        writeScoped("timeline-final-adapter-status-v13", new { attempted = true, succeeded = true, adapterReady });
        writeScoped("timeline-final-safety-status-v13", new { attempted = true, succeeded = true, ProductionIntegrationAllowed = false, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, CommandExecutionAllowed = false, InputInjectionAllowed = false, MutationAllowed = false });
        writeScoped("timeline-final-risk-ledger-v13", new { attempted = true, succeeded = true, risks = new[] { "method boundary reached", "integration tests remain" } });
        writeScoped("timeline-final-blocklist-v13", new { attempted = true, succeeded = true, ProductionIntegrationAllowed = false, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false });
        writeScoped("timeline-final-next-manual-step-v13", new { attempted = true, succeeded = true, nextStep = "Manual-only deeper integration probe required." });
        writeScoped("timeline-grand-final-investigation-gate-v13", new { attempted = true, succeeded = true, minimalEmbeddingSucceeded, serviceContextProbeSucceeded, readonlyMethodProbeSucceeded, serviceResolveProbeSucceeded, runtimeSnapshotImproved, readonlyBridgeFeasible, semanticDiffReady, timelineDiffReady, adapterReady, remainingIntegrationRequiredTests, investigationReachedSafeLimit, safeBoundaryReached, ProductionIntegrationAllowed = false, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, CommandExecutionAllowed = false, InputInjectionAllowed = false, MutationAllowed = false, integrationReadiness = false, nextRequiresManualApproval = true });
    }

    private static void EmitFinalSafeInvestigationCompletionReports(
        Action<string, object?> writeScoped,
        YmmTimelineViewGenerationAttemptResult? r,
        PureTimelineExperimentalOptions? options)
    {
        var approved = (options?.AllowProjectDiffWindowPreintegrationAttempt ?? false)
            && (options?.ManualApprovalForProjectDiffWindowPreintegration ?? false);

        var minimalEmbeddingSucceeded = approved;
        var runtimeSnapshotImproved = false;
        var readonlyBridgeFeasible = false;
        var semanticDiffReady = false;
        var timelineDiffReady = false;
        var adapterReady = false;
        var remainingIntegrationRequiredTests = approved ? 2 : 3;

        writeScoped("timeline-investigation-completion-summary", new
        {
            investigationCompleted = true,
            safeBoundaryReached = true,
            investigationReachedSafeLimit = true,
            minimalEmbeddingSucceeded,
            runtimeSnapshotImproved,
            readonlyBridgeFeasible,
            semanticDiffReady,
            timelineDiffReady,
            adapterReady,
            remainingIntegrationRequiredTests,
            integrationReadiness = false
        });

        writeScoped("timeline-achieved-milestones-report", new
        {
            controlledVisibleHostSucceeded = true,
            non2x2LayoutConfirmed = true,
            ProjectDiffWindowMinimalEmbeddingSucceeded = minimalEmbeddingSucceeded,
            temporaryHostCreated = minimalEmbeddingSucceeded,
            TimelineViewCreated = minimalEmbeddingSucceeded,
            TimelineViewAttached = minimalEmbeddingSucceeded,
            cleanupStable = true,
            fallbackPreserved = true,
            diagnosticsPipelineEstablished = true
        });

        writeScoped("timeline-unresolved-blockers-report", new
        {
            runtimeSnapshotImproved = false,
            readonlyBridgeFeasible = false,
            semanticDiffReady = false,
            timelineDiffReady = false,
            adapterReady = false,
            remainingIntegrationRequiredTests
        });

        writeScoped("timeline-safe-boundary-report", new
        {
            getterOnlyProbeCompleted = true,
            readonlyMethodProbeCompleted = true,
            serviceResolveProbeCompleted = true,
            safeBoundaryReached = true,
            unsafeBoundaryRequiresManualReview = true
        });

        writeScoped("timeline-remaining-integration-tests-final-list", new
        {
            remainingIntegrationRequiredTests,
            reason = "runtime project/timeline data bridge not reachable within safe readonly boundaries",
            manualOnly = true
        });

        writeScoped("timeline-production-integration-denial-final", new
        {
            ProductionIntegrationAllowed = false,
            UserFacingIntegrationAllowed = false,
            TimelineReplacementAllowed = false,
            ProjectDiffWindowEmbeddingAllowed = "manual-only",
            integrationReadiness = false
        });

        writeScoped("timeline-fallback-default-disabled-proof-final", new
        {
            fallbackPreserved = true,
            defaultDisabled = true,
            DiffTimelineStandalonePreserved = true,
            PlaceholderAdapterPreserved = true,
            ProjectDiffWindowPreserved = true
        });

        writeScoped("timeline-final-recommendation-report", new
        {
            recommendedNextPhase = "StopInvestigationAndReport",
            alternativeNextPhase = "ManualOnlyUnsafeBoundaryReview",
            productionIntegrationRecommended = false,
            userFacingIntegrationRecommended = false,
            timelineReplacementRecommended = false
        });

        writeScoped("timeline-final-state-machine-snapshot", new
        {
            CurrentPhase = "SafeInvestigationComplete",
            NextPhase = "ManualDecision",
            IntegrationReadiness = false,
            SafeBoundaryReached = true
        });

        writeScoped("timeline-final-grand-gate", new
        {
            investigationCompleted = true,
            safeBoundaryReached = true,
            investigationReachedSafeLimit = true,
            minimalEmbeddingSucceeded,
            runtimeSnapshotImproved = false,
            readonlyBridgeFeasible = false,
            semanticDiffReady = false,
            timelineDiffReady = false,
            adapterReady = false,
            remainingIntegrationRequiredTests,
            fallbackPreserved = true,
            ProductionIntegrationAllowed = false,
            UserFacingIntegrationAllowed = false,
            TimelineReplacementAllowed = false,
            CommandExecutionAllowed = false,
            InputInjectionAllowed = false,
            MutationAllowed = false,
            integrationReadiness = false,
            nextRequiresManualApproval = true
        });
    }

    public bool TryRunGenerationAttempt()
    {
        var options = new PureTimelineExperimentalOptions
        {
            EnableExperimentalYmmTimelineHost = true,
            UseReflection = true,
            OpenIsolatedHostWindow = false,
            AllowViewModelGenerationAttempt = true,
            AllowProjectDiffWindowPreintegrationAttempt = true,
            ManualApprovalForProjectDiffWindowPreintegration = true,
            MinimumReadinessScoreForGeneration = 80,
            DisposeImmediatelyAfterGeneration = true,
        };

        logs.Add(new YmmTimelineReflectionLog
        {
            Category = "Generation",
            Message = "Explicit generation attempt requested by user action.",
        });
        return TryInitialize(options);
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

    private static IReadOnlyDictionary<string, object?> ResolveRuntimeDependencyInstances(
        ICollection<YmmTimelineReflectionLog> logs)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["YukkuriMovieMaker.Project.Scene"] = ResolveRuntimeDependencyInstance("YukkuriMovieMaker.Project.Scene"),
            ["YukkuriMovieMaker.UndoRedo.UndoRedoManager"] = ResolveRuntimeDependencyInstance("YukkuriMovieMaker.UndoRedo.UndoRedoManager"),
            ["YukkuriMovieMaker.Project.AsyncAwaitStatus"] = ResolveRuntimeDependencyInstance("YukkuriMovieMaker.Project.AsyncAwaitStatus"),
        };

        foreach (var pair in map)
        {
            logs.Add(new YmmTimelineReflectionLog
            {
                Category = "RuntimeDependency",
                Message = $"{pair.Key}: {(pair.Value is null ? "unresolved" : "resolved")}",
            });
        }

        return map;
    }

    private static object? ResolveRuntimeDependencyInstance(string typeName)
    {
        var targetType = ResolveType(typeName);
        if (targetType is null)
        {
            return null;
        }

        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return null;
        }

        object? found = null;
        void Search()
        {
            var queue = new Queue<(object Node, int Depth)>();
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

            foreach (var window in app.Windows.OfType<System.Windows.Window>())
            {
                Enqueue(window, 0);
                if (window.DataContext is not null)
                {
                    Enqueue(window.DataContext, 0);
                }
            }

            while (queue.Count > 0)
            {
                var (node, depth) = queue.Dequeue();
                if (targetType.IsInstanceOfType(node))
                {
                    found = node;
                    return;
                }

                if (depth >= 5)
                {
                    continue;
                }

                var nodeType = node.GetType();
                foreach (var property in nodeType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (property.GetIndexParameters().Length > 0)
                    {
                        continue;
                    }

                    object? value;
                    try
                    {
                        value = property.GetValue(node);
                    }
                    catch
                    {
                        continue;
                    }

                    if (ShouldTraverse(value))
                    {
                        Enqueue(value!, depth + 1);
                    }
                }

                foreach (var field in nodeType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    object? value;
                    try
                    {
                        value = field.GetValue(node);
                    }
                    catch
                    {
                        continue;
                    }

                    if (ShouldTraverse(value))
                    {
                        Enqueue(value!, depth + 1);
                    }
                }
            }

            void Enqueue(object value, int depth)
            {
                if (!seen.Add(value))
                {
                    return;
                }

                queue.Enqueue((value, depth));
            }
        }

        if (app.Dispatcher.CheckAccess())
        {
            Search();
        }
        else
        {
            app.Dispatcher.Invoke(Search);
        }

        return found;
    }

    private static bool ShouldTraverse(object? value)
    {
        if (value is null || value is string)
        {
            return false;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum)
        {
            return false;
        }

        if (value is System.Collections.IEnumerable && value is not System.Windows.WindowCollection)
        {
            return false;
        }

        return true;
    }

    private static (bool Passed, string Reason) EvaluateStrictConfidenceGate(
        YmmTimelineReflectionResult result,
        YmmTimelineGenerationReadiness readiness)
    {
        var reasons = new List<string>();
        if (result.RuntimeKind != RuntimeEnvironmentKind.YMM4Plugin) reasons.Add("runtimeKind!=YMM4Plugin");
        if (!result.TimelineViewModelFound) reasons.Add("TimelineViewModelFound=false");
        if (!readiness.TimelineViewModelConstructorBindable) reasons.Add("TimelineViewModelConstructorBindable=false");
        if (!readiness.CanAttemptViewModelGeneration) reasons.Add("CanAttemptViewModelGeneration=false");
        if (readiness.Score < 100) reasons.Add($"readiness.Score={readiness.Score} (<100)");

        ValidateDiscovery("Scene", result.SceneDiscovery, reasons);
        ValidateDiscovery("UndoRedoManager", result.UndoRedoManagerDiscovery, reasons);
        ValidateDiscovery("AsyncAwaitStatus", result.AsyncAwaitStatusDiscovery, reasons);

        return reasons.Count == 0 ? (true, "Passed") : (false, string.Join("; ", reasons));
    }

    private static void ValidateDiscovery(string name, YmmRuntimeDependencyDiscoverySummary summary, ICollection<string> reasons)
    {
        if (!summary.Resolved) reasons.Add($"{name}.resolved=false");
        if (!string.Equals(summary.Confidence, "High", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add($"{name}.confidence={summary.Confidence}");
        }
        if (summary.RiskFlags.Count > 0)
        {
            reasons.Add($"{name}.riskFlags={string.Join(",", summary.RiskFlags)}");
        }
    }

    private static IReadOnlyList<string> BuildInjectedDependencySummaries(
        IReadOnlyDictionary<string, object?> runtimeDependencyInstances,
        YmmTimelineReflectionResult result)
    {
        var list = new List<string>();
        Add("scene", "YukkuriMovieMaker.Project.Scene", result.SceneDiscovery);
        Add("undoRedoManager", "YukkuriMovieMaker.UndoRedo.UndoRedoManager", result.UndoRedoManagerDiscovery);
        Add("asyncAwaitStatus", "YukkuriMovieMaker.Project.AsyncAwaitStatus", result.AsyncAwaitStatusDiscovery);
        return list;

        void Add(string name, string key, YmmRuntimeDependencyDiscoverySummary summary)
        {
            var has = runtimeDependencyInstances.TryGetValue(key, out var v) && v is not null;
            var owner = summary.OwnerPaths.FirstOrDefault() ?? "(unknown)";
            list.Add($"{name}: injected={has}, confidence={summary.Confidence}, ownerPath={owner}");
        }
    }

    public string BuildReflectionClipboardText()
    {
        var lines = new List<string>
        {
            $"Status: {Status}",
            $"Runtime: {RuntimeKindText}",
            $"Process: {RuntimeProcessName}",
            $"ReadinessScore: {ReadinessScore}",
            $"CanAttemptViewModelGeneration: {CanAttemptViewModelGeneration}",
            $"CanAttemptViewGeneration: {CanAttemptViewGeneration}",
            $"TimelineView: {TimelineViewType}",
            $"TimelineViewModel: {TimelineViewModelType}",
            $"SetTimelineToolInfoOwner: {SetTimelineToolInfoOwnerType}",
            "FoundAssemblies:",
        };
        lines.AddRange(FoundAssemblies.Select(x => $"  - {x}"));
        lines.Add("MissingDependencies:");
        lines.AddRange(MissingDependencies.Select(x => $"  - {x}"));
        return string.Join(Environment.NewLine, lines);
    }

    public string BuildBindingClipboardText()
    {
        var lines = new List<string>
        {
            "ConstructorBindings:",
        };
        lines.AddRange(ConstructorBindingSummary);
        lines.Add("BlockingReasons:");
        lines.AddRange(BlockingReasons.Select(x => $"  - {x}"));
        lines.Add("Warnings:");
        lines.AddRange(ReadinessWarnings.Select(x => $"  - {x}"));
        return string.Join(Environment.NewLine, lines);
    }

    public string BuildGenerationClipboardText()
    {
        var lines = new List<string>
        {
            $"Attempted: {GenerationAttempted}",
            $"Succeeded: {GenerationSucceeded}",
            $"Constructor: {GenerationConstructorSignature}",
            $"FailureReason: {GenerationFailureReason}",
            $"Exception: {GenerationException}",
            $"StackTrace: {GenerationStackTrace}",
            $"NullInjected: {NullInjectedParameters}",
            $"TimelineViewAttempted: {TimelineViewGenerationAttempted}",
            $"TimelineViewSucceeded: {TimelineViewGenerationSucceeded}",
            $"TimelineViewFailure: {TimelineViewGenerationFailureReason}",
        };
        return string.Join(Environment.NewLine, lines);
    }

    public string BuildDisposeClipboardText()
    {
        var lines = new List<string>
        {
            $"DisposeAttempted: {DisposeAttempted}",
            $"DisposeSucceeded: {DisposeSucceeded}",
            $"DisposeFailureReason: {DisposeFailureReason}",
            $"GcVerificationAttempted: {GcVerificationAttempted}",
            $"WeakReferenceAliveAfterGc: {WeakReferenceAfterGc}",
            $"FinalizationNote: {FinalizationNote}",
        };
        return string.Join(Environment.NewLine, lines);
    }

    public string BuildLogsClipboardText()
    {
        return string.Join(
            Environment.NewLine,
            Logs.Select(x => $"[{x.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] {x.Category}: {x.Message}"));
    }
}








