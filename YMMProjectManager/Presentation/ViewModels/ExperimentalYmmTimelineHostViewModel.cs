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
        if (disposed)
        {
            logs.Add(new YmmTimelineReflectionLog { Category = "Init", Message = "Initialization skipped: already disposed." });
            return false;
        }

        initializeStopwatch.Restart();
        logs.Clear();
        logs.Add(new YmmTimelineReflectionLog { Category = "Init", Message = $"Initialize start (useReflection={options.UseReflection})" });
        progress?.Report(5);

        try
        {
            var computed = await Task.Run(async () =>
            {
                var localLogs = new List<YmmTimelineReflectionLog>
                {
                    new() { Category = "Init", Message = $"Initialize start (useReflection={options.UseReflection})" }
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
            if (TimelineViewGenerationAttemptResult is not null)
            {
                SaveTimelineViewGenerationAttemptDiagnostics(result, TimelineViewBindingResults, TimelineViewModelBindingResults, GenerationReadiness, GenerationAttemptResult, TimelineViewGenerationAttemptResult, computed.Logs);
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
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
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
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
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
                reflection = result,
                timelineViewConstructorBindings = viewBindings,
                timelineViewModelConstructorBindings = viewModelBindings,
                readiness,
                generationAttempt = generationAttemptResult,
                timelineViewGenerationAttempt = timelineViewGenerationAttemptResult,
                dataContextBoundary = timelineViewGenerationAttemptResult?.DataContextBoundaryPatterns ?? [],
                commandRouteBoundary = timelineViewGenerationAttemptResult?.CommandRouteBoundary,
                bindingErrorObservationUnavailable = timelineViewGenerationAttemptResult?.BindingErrorObservationUnavailable ?? true,
                visualAttachAttempted = timelineViewGenerationAttemptResult?.VisualAttachAttempted ?? false,
                visualAttachForbidden = timelineViewGenerationAttemptResult?.VisualAttachForbidden ?? true,
                logs = logs.Select(x => new { x.Timestamp, x.Category, x.Message }).ToList(),
            };
            var json = JsonSerializer.Serialize(output, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            var path = Path.Combine(dir, $"timeline-view-datacontext-boundary-{result.RuntimeKind}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, json, Encoding.UTF8);
            var passivePath = Path.Combine(dir, $"timeline-view-passive-event-boundary-{result.RuntimeKind}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(passivePath, json, Encoding.UTF8);
            var commandPath = Path.Combine(dir, $"timeline-view-command-route-boundary-{result.RuntimeKind}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(commandPath, json, Encoding.UTF8);
        }
        catch
        {
        }
    }

    public bool TryRunGenerationAttempt()
    {
        var options = new PureTimelineExperimentalOptions
        {
            EnableExperimentalYmmTimelineHost = true,
            UseReflection = true,
            OpenIsolatedHostWindow = false,
            AllowViewModelGenerationAttempt = true,
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





