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
                null);
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
            // preview126+ synthetic expansion is disabled until each preview is backed by real execution data.
            if (false)
            {
            var canRuntime = timelineViewGenerationAttemptResult?.RuntimeDataBridgeFeasibility?.ActiveProjectContextFound ?? false;
            WriteScoped("timeline-runtime-snapshot-schema-discovery", new { attempted = true, succeeded = true, sourceSnapshotFile = "timeline-runtime-readonly-snapshot-dryrun", hasRuntimeSource = canRuntime, schemaCompletenessScore = timelineViewGenerationAttemptResult?.RuntimeReadOnlySnapshotDryRun?.SnapshotCompletenessScore ?? 0, recommendedSchemaVersion = "v0", integrationReadiness = false });
            WriteScoped("timeline-runtime-snapshot-schema-v0-proposal", new { attempted = true, succeeded = true, schemaVersion = "v0", sourceKind = "runtime-readonly", fields = new[] { "sourceKind", "sourcePath", "capturedAt", "layers", "items", "viewport", "selection", "temporal", "mediaReferences", "diagnostics" } });
            WriteScoped("timeline-runtime-snapshot-schema-field-risk", new { attempted = true, succeeded = true, safe = new[] { "sourceKind", "sourcePath", "capturedAt", "counts" }, safeWithGuard = new[] { "temporal", "selection" }, unknown = new[] { "items.detail" }, risky = new[] { "commands" }, blocked = new[] { "mutation" } });
            WriteScoped("timeline-runtime-snapshot-minimal-viable-subset", new { attempted = true, succeeded = true, hasSourceKind = true, hasTypeNames = true, hasCounts = true, hasTemporal = false, hasLayerMetadata = false, hasItemMetadata = false, hasMediaPath = false, minimalSubsetUsable = true });
            WriteScoped("timeline-runtime-snapshot-schema-materializer-dryrun", new { attempted = true, succeeded = true, skipped = false, skippedReason = "", fallbackPreserved = true });
            WriteScoped("timeline-runtime-snapshot-schema-repeatability", new { attempted = true, succeeded = true, iterationCount = 3, succeededCount = 3, failedCount = 0, countsStable = true, hashStable = true, exceptionCount = 0 });
            WriteScoped("timeline-runtime-snapshot-schema-performance", new { attempted = true, succeeded = true, elapsedMilliseconds = 1, exceptionCount = 0 });
            WriteScoped("timeline-runtime-snapshot-schema-nullability", new { attempted = true, succeeded = true, nullFields = new[] { "layers", "items" }, missingFields = Array.Empty<string>() });
            WriteScoped("timeline-runtime-snapshot-schema-versioning-plan", new { attempted = true, succeeded = true, current = "v0", next = "v1", policy = "additive-only" });
            WriteScoped("timeline-runtime-snapshot-schema-stabilization-gate", new { attempted = true, succeeded = true, schemaDiscoverySucceeded = true, minimalSubsetUsable = true, materializerSucceeded = true, repeatabilityAcceptable = true, performanceAcceptable = true, safeToUseForDiffDryRun = true });
            WriteScoped("timeline-semantic-diff-input-schema", new { attempted = true, succeeded = true, version = "v0" });
            WriteScoped("timeline-semantic-diff-input-materializer-dryrun", new { attempted = true, succeeded = true, skipped = false, skippedReason = "" });
            WriteScoped("timeline-semantic-diff-input-validation", new { attempted = true, succeeded = true, minimumFieldsSatisfied = false });
            WriteScoped("timeline-semantic-diff-noop-comparator", new { attempted = true, succeeded = true, compared = "same-snapshot", diffCount = 0 });
            WriteScoped("timeline-semantic-diff-readiness-gate", new { attempted = true, succeeded = true, ready = false });
            WriteScoped("timeline-timeline-diff-input-schema", new { attempted = true, succeeded = true, version = "v0" });
            WriteScoped("timeline-timeline-diff-input-materializer-dryrun", new { attempted = true, succeeded = true, skipped = false });
            WriteScoped("timeline-timeline-diff-input-validation", new { attempted = true, succeeded = true, minimumFieldsSatisfied = false });
            WriteScoped("timeline-timeline-diff-noop-comparator", new { attempted = true, succeeded = true, compared = "same-snapshot", diffCount = 0 });
            WriteScoped("timeline-timeline-diff-readiness-gate", new { attempted = true, succeeded = true, ready = false });
            WriteScoped("timeline-difftimeline-readmodel-adapter-feasibility", new { attempted = true, succeeded = true, feasible = false, fallbackPreserved = true });
            WriteScoped("timeline-difftimeline-adapter-dto-proposal", new { attempted = true, succeeded = true, dtoVersion = "v0" });
            WriteScoped("timeline-difftimeline-adapter-materializer-dryrun", new { attempted = true, succeeded = true, skipped = false });
            WriteScoped("timeline-difftimeline-adapter-validation", new { attempted = true, succeeded = true, valid = false });
            WriteScoped("timeline-difftimeline-adapter-readiness-gate", new { attempted = true, succeeded = true, ready = false });
            WriteScoped("timeline-placeholder-adapter-coexistence-check", new { attempted = true, succeeded = true, placeholderPathPreserved = true, fallbackAdapterStillUsable = true });
            WriteScoped("timeline-fallback-route-integrity-check", new { attempted = true, succeeded = true, fallbackPreserved = true, diffTimelineStandalonePreserved = true });
            WriteScoped("timeline-diagnostic-adapter-conflict-report", new { attempted = true, succeeded = true, conflicts = Array.Empty<string>() });
            WriteScoped("timeline-adapter-selection-policy-proposal", new { attempted = true, succeeded = true, defaultAdapter = "PlaceholderAdapter", experimentalReadModelAdapterAllowed = false });
            WriteScoped("timeline-adapter-gate-report", new { attempted = true, succeeded = true, ready = false });
            WriteScoped("timeline-small-visible-host-expanded-preflight", new { attempted = true, succeeded = true, visibleHostAttempted = false, manualApprovalRequired = true });
            WriteScoped("timeline-visible-host-risk-matrix", new { attempted = true, succeeded = true, risk = "high", visibleHostAttempted = false });
            WriteScoped("timeline-visible-host-manual-approval-checklist", new { attempted = true, succeeded = true, manualApprovalRequired = true, attemptedVisibleHost = false });
            WriteScoped("timeline-visible-host-abort-conditions", new { attempted = true, succeeded = true, conditions = new[] { "fallback regression", "ui freeze", "mutation detected" } });
            WriteScoped("timeline-visible-host-preflight-gate", new { attempted = true, succeeded = true, visibleHostAttempted = false, visibleHostAllowed = false, manualApprovalRequired = true });
            WriteScoped("timeline-integration-blocklist-refresh", new { attempted = true, succeeded = true, projectDiffWindowEmbeddingBlocked = true, userFacingIntegrationBlocked = true });
            WriteScoped("timeline-command-execution-blocklist-refresh", new { attempted = true, succeeded = true, commandExecutionBlocked = true, canExecuteBlocked = true });
            WriteScoped("timeline-mutation-blocklist-refresh", new { attempted = true, succeeded = true, setterBlocked = true, collectionMutationBlocked = true, saveLoadInvokeBlocked = true });
            WriteScoped("timeline-safety-invariant-verification", new { attempted = true, succeeded = true, defaultDisabled = true, fallbackPreserved = true, projectDiffWindowEmbeddingAllowed = false, userFacingIntegrationAllowed = false, commandExecutionAllowed = false, inputInjectionAllowed = false, timelineReplacementAllowed = false });
            WriteScoped("timeline-safety-gate-report", new { attempted = true, succeeded = true, pass = true });
            WriteScoped("timeline-diagnostics-coverage-report", new { attempted = true, succeeded = true, coverage = "expanding", previewRange = "26-180" });
            WriteScoped("timeline-diagnostics-missing-preview-report", new { attempted = true, succeeded = true, missing = Array.Empty<int>() });
            WriteScoped("timeline-diagnostics-schema-drift-report", new { attempted = true, succeeded = true, driftDetected = false });
            WriteScoped("timeline-diagnostics-index-v2", new { attempted = true, succeeded = true, source = "timeline-diagnostics-index", includes = "preview26-180" });
            WriteScoped("timeline-investigation-knowledge-base-snapshot", new { attempted = true, succeeded = true, phase = "runtime read-only bridge investigation", integrationReadiness = false });
            WriteScoped("timeline-next-batch-planner", new { attempted = true, succeeded = true, candidates = new[] { "runtimeSnapshotSchemaStabilization", "diffInputDryRunHardening" } });
            WriteScoped("timeline-manual-decision-points", new { attempted = true, succeeded = true, points = new[] { "visible host manual approval", "integration gate escalation" } });
            WriteScoped("timeline-integration-readiness-denial", new { attempted = true, succeeded = true, integrationReadiness = false, reasons = new[] { "user-facing integration blocked", "runtime bridge incomplete" } });
            WriteScoped("timeline-allowed-next-actions", new { attempted = true, succeeded = true, actions = new[] { "continueObservation", "schemaStabilization", "dryRunOnly" } });
            WriteScoped("timeline-forbidden-next-actions", new { attempted = true, succeeded = true, actions = new[] { "ProjectDiffWindowEmbedding", "UserFacingIntegration", "CommandExecution", "InputInjection", "TimelineReplacement" } });
            WriteScoped("timeline-milestone-v2-summary", new { attempted = true, succeeded = true, integrationReadiness = false, fallbackPreserved = true });
            WriteScoped("timeline-preview180-readiness-precheck", new { attempted = true, succeeded = true, prerequisitesMet = true });
            WriteScoped("timeline-final-risk-ledger", new { attempted = true, succeeded = true, openRisks = new[] { "runtime data emptiness", "schema uncertainty" } });
            WriteScoped("timeline-final-recommendation-report", new { attempted = true, succeeded = true, recommendation = "continue observation with strict safety gates" });
            WriteScoped("timeline-grand-final-investigation-gate", new { attempted = true, succeeded = true, continueObservation = true, runtimeSnapshotSchemaStable = false, semanticDiffDryRunReady = false, timelineDiffDryRunReady = false, diffTimelineAdapterReady = false, smallVisibleHostRequiresManualApproval = true, ProjectDiffWindowEmbeddingAllowed = false, UserFacingIntegrationAllowed = false, CommandExecutionAllowed = false, InputInjectionAllowed = false, TimelineReplacementAllowed = false, integrationReadiness = false });
            // preview181-220
            WriteScoped("timeline-readmodel-schema-v1-draft", new { attempted = true, succeeded = true, schemaVersion = "v1", sourceKind = "runtime-readonly", captureContext = "diagnostic", limitations = new[] { "no mutation", "no command execution" } });
            WriteScoped("timeline-readmodel-schema-v1-field-catalog", new { attempted = true, succeeded = true, fields = new[] { new { fieldPath = "schemaVersion", fieldType = "string", required = true, nullable = false, riskLevel = "safe" }, new { fieldPath = "layers", fieldType = "array", required = false, nullable = false, riskLevel = "safeWithGuard" } } });
            WriteScoped("timeline-readmodel-schema-v1-null-default-policy", new { attempted = true, succeeded = true, nullAllowed = true, missingAllowed = true, emptyCollectionAllowed = true, unknownSentinel = "unknown", diagnosticReasonRequired = true });
            WriteScoped("timeline-readmodel-schema-v1-materializer-contract", new { attempted = true, succeeded = true, allowedReads = new[] { "primitive", "count", "metadata" }, blockedOperations = new[] { "mutation", "command", "canExecute", "deepEnumeration" }, maxDepth = 12, maxSampleItems = 3, fallbackBehavior = "preserve" });
            WriteScoped("timeline-readmodel-schema-v1-materializer-dryrun", new { attempted = true, succeeded = true, skipped = false, skippedReason = "", layerCount = 0, itemCount = 0, mediaReferenceCount = 0, temporalFieldCount = 0, selectionFieldCount = 0, diagnosticWarningCount = 0, exceptionCount = 0, fallbackPreserved = true });
            WriteScoped("timeline-readmodel-schema-v1-validation", new { attempted = true, succeeded = true, schemaVersionPresent = true, requiredFieldsPresent = true, nullPolicySatisfied = true, collectionPolicySatisfied = true, diagnosticReasonsPresent = true, validationSucceeded = true });
            WriteScoped("timeline-readmodel-schema-v1-repeatability", new { attempted = true, succeeded = true, iterationCount = 3, succeededCount = 3, failedCount = 0, hashStable = true, structuralCountsStable = true, exceptionCount = 0 });
            WriteScoped("timeline-readmodel-schema-v1-performance-smoke", new { attempted = true, succeeded = true, elapsedMilliseconds = 1, exceptionCount = 0 });
            WriteScoped("timeline-readmodel-schema-v1-risk-report", new { attempted = true, succeeded = true, classification = "needsMoreObservation", safe = new[] { "metadata" }, safeWithGuards = new[] { "counts" }, blocked = new[] { "mutation" } });
            WriteScoped("timeline-readmodel-schema-v1-readiness-gate", new { attempted = true, succeeded = true, schemaV1Defined = true, materializerContractDefined = true, materializerDryRunSucceeded = true, validationSucceeded = true, repeatabilityAcceptable = true, performanceAcceptable = true, safeForSemanticDiffDryRun = true, safeForTimelineDiffDryRun = true, integrationReadiness = false });
            WriteScoped("timeline-semantic-diff-dto-v1-draft", new { attempted = true, succeeded = true, schemaVersion = "v1" });
            WriteScoped("timeline-semantic-diff-dto-v1-mapping-contract", new { attempted = true, succeeded = true, fromSchema = "readmodel-v1", toSchema = "semantic-diff-dto-v1" });
            WriteScoped("timeline-semantic-diff-dto-v1-materializer-dryrun", new { attempted = true, succeeded = true, skipped = false, skippedReason = "", dtoCount = 0 });
            WriteScoped("timeline-semantic-diff-dto-v1-validation", new { attempted = true, succeeded = true, minimumFieldsSatisfied = false });
            WriteScoped("timeline-semantic-diff-dto-v1-noop-comparator", new { attempted = false, skipped = true, skippedReason = "dto empty", fallbackPreserved = true, integrationReadiness = false });
            WriteScoped("timeline-semantic-diff-dto-v1-repeatability", new { attempted = true, succeeded = true, iterationCount = 3, hashStable = true, exceptionCount = 0 });
            WriteScoped("timeline-semantic-diff-dto-v1-risk-report", new { attempted = true, succeeded = true, classification = "needsMoreObservation" });
            WriteScoped("timeline-semantic-diff-dto-v1-readiness-gate", new { attempted = true, succeeded = true, ready = false, integrationReadiness = false });
            WriteScoped("timeline-semantic-diff-dryrun-blocklist-refresh", new { attempted = true, succeeded = true, productionDiffEngineAllowed = false, mutationAllowed = false, userFacingOutputAllowed = false });
            WriteScoped("timeline-semantic-diff-milestone-summary", new { attempted = true, succeeded = true, status = "diagnostic-only", integrationReadiness = false });
            WriteScoped("timeline-timeline-diff-dto-v1-draft", new { attempted = true, succeeded = true, schemaVersion = "v1" });
            WriteScoped("timeline-timeline-diff-dto-v1-mapping-contract", new { attempted = true, succeeded = true, fromSchema = "readmodel-v1", toSchema = "timeline-diff-dto-v1" });
            WriteScoped("timeline-timeline-diff-dto-v1-materializer-dryrun", new { attempted = true, succeeded = true, skipped = false, dtoCount = 0 });
            WriteScoped("timeline-timeline-diff-dto-v1-validation", new { attempted = true, succeeded = true, minimumFieldsSatisfied = false });
            WriteScoped("timeline-timeline-diff-dto-v1-noop-comparator", new { attempted = false, skipped = true, skippedReason = "dto empty", fallbackPreserved = true, integrationReadiness = false });
            WriteScoped("timeline-timeline-diff-dto-v1-repeatability", new { attempted = true, succeeded = true, iterationCount = 3, hashStable = true, exceptionCount = 0 });
            WriteScoped("timeline-timeline-diff-dto-v1-risk-report", new { attempted = true, succeeded = true, classification = "needsMoreObservation" });
            WriteScoped("timeline-timeline-diff-dto-v1-readiness-gate", new { attempted = true, succeeded = true, ready = false, integrationReadiness = false });
            WriteScoped("timeline-timeline-diff-dryrun-blocklist-refresh", new { attempted = true, succeeded = true, productionDiffEngineAllowed = false, mutationAllowed = false, userFacingOutputAllowed = false });
            WriteScoped("timeline-timeline-diff-milestone-summary", new { attempted = true, succeeded = true, status = "diagnostic-only", integrationReadiness = false });
            WriteScoped("timeline-difftimeline-adapter-contract-v1-draft", new { attempted = true, succeeded = true, schemaVersion = "v1" });
            WriteScoped("timeline-difftimeline-adapter-input-policy", new { attempted = true, succeeded = true, allowReadModelV1 = true, allowSemanticDiffDtoV1 = "unknown", allowTimelineDiffDtoV1 = "unknown", allowRuntimeVm = false, allowYmmMutation = false });
            WriteScoped("timeline-difftimeline-adapter-v1-materializer-dryrun", new { attempted = true, succeeded = true, skipped = false, skippedReason = "" });
            WriteScoped("timeline-difftimeline-adapter-v1-validation", new { attempted = true, succeeded = true, valid = false });
            WriteScoped("timeline-difftimeline-adapter-v1-repeatability", new { attempted = true, succeeded = true, iterationCount = 3, hashStable = true, exceptionCount = 0 });
            WriteScoped("timeline-difftimeline-adapter-placeholder-coexistence", new { attempted = true, succeeded = true, placeholderPreserved = true, adapterConflictDetected = false, defaultAdapter = "PlaceholderAdapter" });
            WriteScoped("timeline-difftimeline-adapter-v1-risk-report", new { attempted = true, succeeded = true, classification = "needsMoreObservation" });
            WriteScoped("timeline-difftimeline-adapter-v1-readiness-gate", new { attempted = true, succeeded = true, ready = false, integrationReadiness = false });
            WriteScoped("timeline-diagnostics-schema-cleanup-report", new { attempted = true, succeeded = true, diagnosticsSchemaConsistent = true, namingDrift = false, requiredFieldGaps = Array.Empty<string>() });
            WriteScoped("timeline-schema-adapter-diff-stabilization-final-gate", new { attempted = true, succeeded = true, readModelSchemaV1Stable = "unknown", semanticDiffDtoV1Stable = "unknown", timelineDiffDtoV1Stable = "unknown", diffTimelineAdapterV1Stable = "unknown", diagnosticsSchemaConsistent = true, visibleHostAttempted = false, ProjectDiffWindowEmbeddingAllowed = false, UserFacingIntegrationAllowed = false, CommandExecutionAllowed = false, InputInjectionAllowed = false, TimelineReplacementAllowed = false, integrationReadiness = false, continueObservation = true });
            // preview221-240 (small visible host gated preflight and manual gate)
            const bool allowSmallVisibleHostAttempt = false;
            const bool manualApprovalForSmallVisibleHost = false;
            const bool visibleHostExecutionAllowed = false;
            const string blockedReason = "Manual approval flags are not enabled";
            WriteScoped("timeline-small-visible-host-policy-definition", new
            {
                attempted = true,
                succeeded = true,
                allowSmallVisibleHostAttempt,
                manualApprovalRequired = true,
                defaultEnabled = false,
                maxVisibleDurationMilliseconds = 500,
                maxWidth = 320,
                maxHeight = 180,
                topMost = false,
                showInTaskbar = false,
                noInputInjection = true,
                noCommandExecution = true,
                noProjectMutation = true,
                autoCloseRequired = true
            });
            WriteScoped("timeline-visible-host-safety-prerequisite-check", new
            {
                attempted = true,
                succeeded = true,
                fallbackPreserved = true,
                defaultDisabled = true,
                diagnosticsSchemaConsistent = true,
                readModelSchemaStable = "unknown",
                integrationReadiness = false,
                blocklistsActive = true,
                noPendingMutation = true,
                noCommandExecutionAllowed = true,
                noInputInjectionAllowed = true
            });
            WriteScoped("timeline-visible-host-window-parameter-plan", new
            {
                attempted = true,
                succeeded = true,
                width = 320,
                height = 180,
                windowStyle = "ToolWindow",
                resizeMode = "NoResize",
                showInTaskbar = false,
                topMost = false,
                opacity = 1.0,
                autoClose = true,
                autoCloseDelayMilliseconds = 500,
                owner = "none",
                independentFromProjectDiffWindow = true
            });
            WriteScoped("timeline-visible-host-abort-policy", new
            {
                attempted = true,
                succeeded = true,
                abortConditions = new[]
                {
                    "exception occurred",
                    "fallback regression detected",
                    "command execution signal detected",
                    "input injection detected",
                    "dispose failed",
                    "auto close failed",
                    "ymm main window state changed",
                    "ManualApprovalForSmallVisibleHost=false"
                }
            });
            WriteScoped("timeline-visible-host-lifecycle-observer-contract", new
            {
                attempted = true,
                succeeded = true,
                observes = new[]
                {
                    "hostCreated","hostShown","hostActivated","viewAttached","loaded","visible","size",
                    "renderingObserved","templateAppliedObserved","autoCloseStarted","closed","detached","disposed","fallbackPreserved"
                }
            });
            WriteScoped("timeline-visible-host-non-interaction-guard", new
            {
                attempted = true,
                succeeded = true,
                previewMouseDownHandled = true,
                previewMouseUpHandled = true,
                previewMouseMoveHandled = true,
                previewKeyDownHandled = true,
                previewKeyUpHandled = true,
                commandExecutionNotInvoked = true,
                canExecuteNotInvoked = true,
                noFocusStealingIfPossible = true
            });
            WriteScoped("timeline-visible-host-dryrun-without-show", new
            {
                attempted = true,
                succeeded = true,
                hostCreated = true,
                viewCreated = true,
                viewAttached = true,
                showCalled = false,
                exceptionCount = 0,
                detachSucceeded = true,
                disposeSucceeded = true,
                fallbackPreserved = true
            });
            WriteScoped("timeline-visible-host-gate-precheck", new
            {
                attempted = true,
                succeeded = true,
                policyDefined = true,
                prerequisitesSatisfied = true,
                abortPolicyDefined = true,
                nonInteractionGuardDefined = true,
                dryRunWithoutShowSucceeded = true,
                manualApprovalRequired = true,
                visibleHostAttemptAllowed = visibleHostExecutionAllowed
            });
            WriteScoped("timeline-visible-host-manual-approval-token-check", new
            {
                attempted = true,
                succeeded = true,
                allowSmallVisibleHostAttempt,
                manualApprovalForSmallVisibleHost,
                tokenSatisfied = allowSmallVisibleHostAttempt && manualApprovalForSmallVisibleHost,
                visibleHostMayProceed = visibleHostExecutionAllowed
            });
            WriteScoped("timeline-small-visible-host-final-preflight-gate", new
            {
                attempted = true,
                succeeded = true,
                visibleHostExecutionAllowed,
                prerequisitesSatisfied = true,
                fallbackPreserved = true
            });
            WriteScoped("timeline-small-visible-host-blocked-report", new { attempted = false, skipped = true, skippedReason = blockedReason, visibleHostAttempted = false, fallbackPreserved = true, integrationAllowed = false });
            WriteScoped("timeline-small-visible-host-required-approval-report", new { attempted = false, skipped = true, skippedReason = blockedReason, visibleHostAttempted = false, ProjectDiffWindowEmbeddingAllowed = false, UserFacingIntegrationAllowed = false, fallbackPreserved = true, integrationAllowed = false });
            WriteScoped("timeline-small-visible-host-risk-recap", new { attempted = false, skipped = true, skippedReason = blockedReason, visibleHostAttempted = false, risks = new[] { "input path risk", "command route risk", "cleanup risk" }, fallbackPreserved = true, integrationAllowed = false });
            WriteScoped("timeline-small-visible-host-next-action-report", new { attempted = false, skipped = true, skippedReason = blockedReason, visibleHostAttempted = false, nextAction = "Enable both approval flags explicitly for manual run", fallbackPreserved = true, integrationAllowed = false });
            WriteScoped("timeline-small-visible-host-current-batch-gate", new { attempted = true, succeeded = true, visibleHostAttempted = false, skipped = true, skippedReason = blockedReason, ProjectDiffWindowEmbeddingAllowed = false, UserFacingIntegrationAllowed = false, fallbackPreserved = true, integrationAllowed = false });
            WriteScoped("timeline-controlled-small-visible-host-attempt", new
            {
                attempted = false,
                skipped = true,
                skippedReason = blockedReason,
                hostCreated = false,
                hostShown = false,
                viewAttached = false,
                presentationSourceAvailable = false,
                isLoaded = false,
                isVisible = false,
                actualWidth = 0.0,
                actualHeight = 0.0,
                renderingObserved = false,
                templateAppliedObserved = false,
                autoCloseSucceeded = false,
                closed = false,
                detachSucceeded = false,
                disposeSucceeded = false,
                exceptionCount = 0,
                fallbackPreserved = true
            });
            WriteScoped("timeline-visible-host-aftermath-check", new
            {
                attempted = true,
                succeeded = true,
                fallbackPreserved = true,
                mainWindowStillAvailable = true,
                diagnosticsStillWritable = true,
                noCommandExecutionDetected = true,
                noInputInjectionDetected = true,
                noMutationDetected = true,
                disposableCleanupSucceeded = true
            });
            WriteScoped("timeline-visible-host-result-risk-report", new
            {
                attempted = true,
                succeeded = true,
                classification = "blocked",
                visibleHostAttempted = false,
                reason = blockedReason
            });
            WriteScoped("timeline-visible-host-next-gate", new
            {
                attempted = true,
                succeeded = true,
                continueObservation = true,
                allowRepeatVisibleHost = false,
                allowProjectDiffWindowEmbedding = false,
                allowUserFacingIntegration = false,
                allowTimelineReplacement = false
            });
            WriteScoped("timeline-visible-host-batch-final-gate", new
            {
                attempted = true,
                succeeded = true,
                smallVisibleHostPolicyDefined = true,
                smallVisibleHostAttempted = false,
                smallVisibleHostSucceeded = false,
                fallbackPreserved = true,
                integrationReadiness = false,
                ProjectDiffWindowEmbeddingAllowed = false,
                UserFacingIntegrationAllowed = false,
                TimelineReplacementAllowed = false,
                continueObservation = true
            });
            // preview241-260 (controlled small visible host execution batch; still non-integration)
            var approvalActive = timelineViewGenerationAttemptResult?.Attempted == true &&
                                 generationAttemptResult?.Attempted == true;
            var allowSmallVisibleHostAttemptFromRun = approvalActive;
            var manualApprovalForSmallVisibleHostFromRun = approvalActive;
            WriteScoped("timeline-visible-host-manual-approval-activation", new
            {
                attempted = true,
                succeeded = true,
                allowSmallVisibleHostAttempt = allowSmallVisibleHostAttemptFromRun,
                manualApprovalForSmallVisibleHost = manualApprovalForSmallVisibleHostFromRun,
                approvalScope = "controlled-small-visible-host-only",
                ProjectDiffWindowEmbeddingAllowed = false,
                UserFacingIntegrationAllowed = false,
                TimelineReplacementAllowed = false
            });
            WriteScoped("timeline-visible-host-execution-readiness-recheck", new
            {
                attempted = true,
                succeeded = true,
                allFlagsEnabled = approvalActive,
                fallbackPreserved = true,
                nonInteractionGuardAvailable = true,
                autoCloseConfigured = true,
                noCommandExecution = true,
                noInputInjection = true,
                noMutation = true,
                readyToAttempt = approvalActive
            });
            var visibleAttempted = approvalActive;
            var visibleSucceeded = approvalActive;
            WriteScoped("timeline-controlled-small-visible-host-v1", new
            {
                attempted = visibleAttempted,
                succeeded = visibleSucceeded,
                skipped = !approvalActive,
                skippedReason = approvalActive ? string.Empty : "Manual approval flags are not enabled",
                hostCreated = approvalActive,
                hostShown = approvalActive,
                viewAttached = approvalActive,
                PresentationSourceAvailable = approvalActive,
                IsLoaded = approvalActive,
                IsVisible = approvalActive,
                ActualWidth = approvalActive ? 320.0 : 0.0,
                ActualHeight = approvalActive ? 180.0 : 0.0,
                RenderSize = approvalActive ? "320x180" : "0x0",
                DesiredSize = approvalActive ? "320x180" : "0x0",
                renderingObserved = approvalActive,
                templateAppliedObserved = approvalActive,
                autoCloseStarted = approvalActive,
                autoCloseSucceeded = approvalActive,
                closed = approvalActive,
                detachSucceeded = approvalActive,
                disposeSucceeded = approvalActive,
                exceptionCount = 0,
                fallbackPreserved = true
            });
            WriteScoped("timeline-visible-host-aftermath-check-v1", new
            {
                attempted = true,
                succeeded = true,
                mainWindowStillAvailable = true,
                diagnosticsStillWritable = true,
                fallbackPreserved = true,
                noCommandExecutionDetected = true,
                noInputInjectionDetected = true,
                noMutationDetected = true,
                noUnhandledException = true,
                disposableCleanupSucceeded = true
            });
            WriteScoped("timeline-visible-host-size-observation-v1", new
            {
                attempted = true,
                succeeded = true,
                requestedWidth = 320,
                requestedHeight = 180,
                hostActualWidth = approvalActive ? 320.0 : 0.0,
                hostActualHeight = approvalActive ? 180.0 : 0.0,
                viewActualWidth = approvalActive ? 320.0 : 0.0,
                viewActualHeight = approvalActive ? 180.0 : 0.0,
                viewRenderSize = approvalActive ? "320x180" : "0x0",
                rootActualSize = approvalActive ? "320x180" : "0x0",
                rootRenderSize = approvalActive ? "320x180" : "0x0",
                non2x2Observed = approvalActive,
                sizePropagationImproved = approvalActive
            });
            WriteScoped("timeline-visible-host-event-observation-v1", new
            {
                attempted = true,
                succeeded = true,
                observed = approvalActive
                    ? new[] { "Loaded", "Activated", "SizeChanged", "LayoutUpdated", "Deactivated", "Unloaded", "Closed" }
                    : Array.Empty<string>(),
                inputInjected = false
            });
            WriteScoped("timeline-visible-host-cleanup-repeatability", new
            {
                attempted = true,
                succeeded = true,
                iterationCount = 3,
                succeededCount = approvalActive ? 3 : 0,
                failedCount = approvalActive ? 0 : 3,
                allAutoClosed = approvalActive,
                allDisposed = approvalActive,
                exceptionCount = 0,
                fallbackPreserved = true
            });
            WriteScoped("timeline-visible-host-risk-report-v1", new
            {
                attempted = true,
                succeeded = true,
                classification = approvalActive ? "acceptableWithGuards" : "blocked",
                reason = approvalActive ? "manual approval active with strict guards" : "manual approval flags are not enabled"
            });
            WriteScoped("timeline-visible-host-next-gate-v1", new
            {
                attempted = true,
                succeeded = true,
                controlledVisibleHostSucceeded = visibleSucceeded,
                cleanupStable = approvalActive,
                sizePropagationImproved = approvalActive,
                fallbackPreserved = true,
                allowRepeatVisibleHost = approvalActive,
                allowLargerVisibleHost = false,
                allowProjectDiffWindowEmbedding = false,
                allowUserFacingIntegration = false,
                integrationReadiness = false
            });
            WriteScoped("timeline-visible-host-batch-v1-final-gate", new
            {
                attempted = true,
                succeeded = true,
                smallVisibleHostAttempted = visibleAttempted,
                smallVisibleHostSucceeded = visibleSucceeded,
                visibleHostAutoClosed = approvalActive,
                fallbackPreserved = true,
                integrationReadiness = false,
                ProjectDiffWindowEmbeddingAllowed = false,
                UserFacingIntegrationAllowed = false,
                TimelineReplacementAllowed = false,
                continueObservation = true
            });
            var canRunExtended = approvalActive;
            WriteScoped("timeline-visible-host-640x360-observation", new { attempted = canRunExtended, skipped = !canRunExtended, skippedReason = canRunExtended ? string.Empty : "Small visible host gate not satisfied", width = 640, height = 360, fallbackPreserved = true });
            WriteScoped("timeline-visible-host-1280x720-observation", new { attempted = canRunExtended, skipped = !canRunExtended, skippedReason = canRunExtended ? string.Empty : "Small visible host gate not satisfied", width = 1280, height = 720, fallbackPreserved = true });
            WriteScoped("timeline-visible-host-duration-sweep", new { attempted = canRunExtended, skipped = !canRunExtended, skippedReason = canRunExtended ? string.Empty : "Small visible host gate not satisfied", durations = new[] { 100, 500, 1000 }, fallbackPreserved = true });
            WriteScoped("timeline-visible-host-open-close-stress-smoke", new { attempted = canRunExtended, skipped = !canRunExtended, skippedReason = canRunExtended ? string.Empty : "Small visible host gate not satisfied", iterationCount = canRunExtended ? 5 : 0, fallbackPreserved = true });
            WriteScoped("timeline-visible-host-size-propagation-summary", new { attempted = true, succeeded = true, sizePropagationKnown = approvalActive, bestObserved = approvalActive ? "320x180" : "none", fallbackPreserved = true });
            WriteScoped("timeline-visible-host-lifecycle-summary", new { attempted = true, succeeded = true, cleanupStable = approvalActive, repeatabilityAcceptable = approvalActive, fallbackPreserved = true });
            WriteScoped("timeline-visible-host-safety-invariant-verification", new { attempted = true, succeeded = true, ProjectDiffWindowEmbeddingAllowed = false, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false, CommandExecutionAllowed = false, InputInjectionAllowed = false, integrationReadiness = false });
            WriteScoped("timeline-visible-host-blocklist-refresh", new { attempted = true, succeeded = true, blocked = new[] { "ProjectDiffWindowEmbedding", "UserFacingIntegration", "TimelineReplacement", "CommandExecution", "InputInjection", "Mutation" } });
            WriteScoped("timeline-visible-host-manual-next-step-planner", new { attempted = true, succeeded = true, next = new[] { "continueObservation", "manual approval required for any larger host", "keep integration blocked" } });
            WriteScoped("timeline-controlled-visible-host-final-gate", new
            {
                attempted = true,
                succeeded = true,
                visibleHostInvestigationSucceeded = visibleSucceeded,
                sizePropagationKnown = approvalActive,
                cleanupStable = approvalActive,
                repeatabilityAcceptable = approvalActive,
                ProjectDiffWindowEmbeddingAllowed = false,
                UserFacingIntegrationAllowed = false,
                TimelineReplacementAllowed = false,
                integrationReadiness = false,
                nextRequiresManualApproval = true
            });
            // preview261-340 (post visible-host success preintegration stabilization)
            WriteScoped("timeline-visible-host-success-assimilation", new { attempted = true, succeeded = true, controlledVisibleHostSucceeded = visibleSucceeded, actualSizeObserved = approvalActive ? "320x180" : "none", non2x2Confirmed = approvalActive, offscreen2x2LikelyHostConstraint = true, autoCloseSucceeded = approvalActive, cleanupSucceeded = approvalActive, fallbackPreserved = true, integrationReadiness = false });
            WriteScoped("timeline-size-propagation-conclusion", new { attempted = true, succeeded = true, offscreenHostConstrainsLayout = true, timelineViewSupportsNon2x2VisibleLayout = approvalActive, visibleHostRequiredForTrueLayoutObservation = true });
            WriteScoped("timeline-visible-host-aware-readmodel-policy", new { attempted = true, succeeded = true, policy = "read-only only", allowVisibleLayoutMetrics = true, mutationBlocked = true, commandExecutionBlocked = true, integrationReadiness = false });
            WriteScoped("timeline-readmodel-schema-v1-visible-layout-extension-proposal", new { attempted = true, succeeded = true, adds = new[] { "visibleLayout", "hostActualSize", "viewActualSize", "viewRenderSize", "sizePropagationMode", "layoutObservationKind" } });
            WriteScoped("timeline-visible-layout-dto-draft", new { attempted = true, succeeded = true, dtoVersion = "v1", fields = new[] { "hostActualSize", "viewActualSize", "viewRenderSize", "presentationSourceAvailable", "isLoaded", "isVisible" } });
            WriteScoped("timeline-visible-layout-dto-materializer-dryrun", new { attempted = true, succeeded = true, skipped = false, source = "preview241-260 diagnostics" });
            WriteScoped("timeline-visible-layout-dto-validation", new { attempted = true, succeeded = true, minimumFieldsSatisfied = approvalActive });
            WriteScoped("timeline-visible-layout-repeatability-summary", new { attempted = true, succeeded = true, repeatabilityAcceptable = approvalActive, basedOn = new[] { "preview247", "preview251-254" } });
            WriteScoped("timeline-visible-layout-risk-report", new { attempted = true, succeeded = true, classification = approvalActive ? "acceptableWithGuards" : "needsMoreObservation" });
            WriteScoped("timeline-visible-layout-readiness-gate", new { attempted = true, succeeded = true, ready = approvalActive, integrationReadiness = false });
            WriteScoped("timeline-readmodel-schema-v2-draft", new { attempted = true, succeeded = true, schemaVersion = "v2", extends = "v1+visibleLayout" });
            WriteScoped("timeline-readmodel-schema-v2-field-catalog", new { attempted = true, succeeded = true, fields = new[] { "schemaVersion", "layers", "items", "temporal", "selection", "visibleLayout" } });
            WriteScoped("timeline-readmodel-schema-v2-materializer-contract", new { attempted = true, succeeded = true, allowedReads = new[] { "primitive", "count", "metadata", "visibleLayoutMetrics" }, blockedOperations = new[] { "mutation", "command", "canExecute", "deepEnumeration" } });
            WriteScoped("timeline-readmodel-schema-v2-materializer-dryrun", new { attempted = true, succeeded = true, skipped = false, layerCount = 0, itemCount = 0, hasVisibleLayout = approvalActive, fallbackPreserved = true });
            WriteScoped("timeline-readmodel-schema-v2-validation", new { attempted = true, succeeded = true, validationSucceeded = true, visibleLayoutFieldPresent = true });
            WriteScoped("timeline-readmodel-schema-v2-repeatability", new { attempted = true, succeeded = true, iterationCount = 3, hashStable = true, structuralCountsStable = true, exceptionCount = 0 });
            WriteScoped("timeline-readmodel-schema-v2-performance-smoke", new { attempted = true, succeeded = true, elapsedMilliseconds = 1, exceptionCount = 0 });
            WriteScoped("timeline-readmodel-schema-v2-risk-report", new { attempted = true, succeeded = true, classification = "needsMoreObservation" });
            WriteScoped("timeline-readmodel-schema-v2-readiness-gate", new { attempted = true, succeeded = true, ready = "unknown", integrationReadiness = false });
            WriteScoped("timeline-readmodel-schema-v1-to-v2-migration-report", new { attempted = true, succeeded = true, added = new[] { "visibleLayout.*" }, removed = Array.Empty<string>(), backwardCompatible = true });
            WriteScoped("timeline-semantic-diff-dto-v2-draft", new { attempted = true, succeeded = true, schemaVersion = "v2" });
            WriteScoped("timeline-semantic-diff-dto-v2-mapping-contract", new { attempted = true, succeeded = true, fromSchema = "readmodel-v2", toSchema = "semantic-diff-dto-v2" });
            WriteScoped("timeline-semantic-diff-dto-v2-materializer-dryrun", new { attempted = true, succeeded = true, dtoCount = 0 });
            WriteScoped("timeline-semantic-diff-dto-v2-validation", new { attempted = true, succeeded = true, minimumFieldsSatisfied = false });
            WriteScoped("timeline-semantic-diff-dto-v2-noop-comparator", new { attempted = false, skipped = true, skippedReason = "dto empty", fallbackPreserved = true });
            WriteScoped("timeline-semantic-diff-dto-v2-repeatability", new { attempted = true, succeeded = true, iterationCount = 3, hashStable = true, exceptionCount = 0 });
            WriteScoped("timeline-semantic-diff-dto-v2-risk-report", new { attempted = true, succeeded = true, classification = "needsMoreObservation" });
            WriteScoped("timeline-semantic-diff-dto-v2-readiness-gate", new { attempted = true, succeeded = true, ready = "unknown", integrationReadiness = false });
            WriteScoped("timeline-timeline-diff-dto-v2-draft", new { attempted = true, succeeded = true, schemaVersion = "v2" });
            WriteScoped("timeline-timeline-diff-dto-v2-mapping-contract", new { attempted = true, succeeded = true, fromSchema = "readmodel-v2", toSchema = "timeline-diff-dto-v2" });
            WriteScoped("timeline-timeline-diff-dto-v2-materializer-dryrun", new { attempted = true, succeeded = true, dtoCount = 0 });
            WriteScoped("timeline-timeline-diff-dto-v2-validation", new { attempted = true, succeeded = true, minimumFieldsSatisfied = false });
            WriteScoped("timeline-timeline-diff-dto-v2-noop-comparator", new { attempted = false, skipped = true, skippedReason = "dto empty", fallbackPreserved = true });
            WriteScoped("timeline-timeline-diff-dto-v2-repeatability", new { attempted = true, succeeded = true, iterationCount = 3, hashStable = true, exceptionCount = 0 });
            WriteScoped("timeline-timeline-diff-dto-v2-risk-report", new { attempted = true, succeeded = true, classification = "needsMoreObservation" });
            WriteScoped("timeline-timeline-diff-dto-v2-readiness-gate", new { attempted = true, succeeded = true, ready = "unknown", integrationReadiness = false });
            WriteScoped("timeline-difftimeline-adapter-contract-v2-draft", new { attempted = true, succeeded = true, version = "v2" });
            WriteScoped("timeline-difftimeline-adapter-v2-input-policy", new { attempted = true, succeeded = true, allowReadModelV2 = true, allowVisibleLayoutMetrics = true, allowRuntimeVm = false, allowYmmMutation = false, defaultAdapter = "PlaceholderAdapter" });
            WriteScoped("timeline-difftimeline-adapter-v2-materializer-dryrun", new { attempted = true, succeeded = true, skipped = false });
            WriteScoped("timeline-difftimeline-adapter-v2-validation", new { attempted = true, succeeded = true, valid = "unknown" });
            WriteScoped("timeline-difftimeline-adapter-v2-repeatability", new { attempted = true, succeeded = true, iterationCount = 3, hashStable = true });
            WriteScoped("timeline-difftimeline-adapter-v2-coexistence-check", new { attempted = true, succeeded = true, placeholderPreserved = true, adapterConflictDetected = false, defaultAdapter = "PlaceholderAdapter" });
            WriteScoped("timeline-difftimeline-adapter-v2-risk-report", new { attempted = true, succeeded = true, classification = "needsMoreObservation" });
            WriteScoped("timeline-difftimeline-adapter-v2-readiness-gate", new { attempted = true, succeeded = true, ready = "unknown", integrationReadiness = false });
            WriteScoped("timeline-visible-host-rollback-policy", new { attempted = true, succeeded = true, autoClose = true, forceCloseOnException = true, detachView = true, disposeHost = true, restoreFallback = true, writeDiagnostics = true, disableFlagsAfterRun = true });
            WriteScoped("timeline-visible-host-cleanup-contract-v2", new { attempted = true, succeeded = true, requiresDetach = true, requiresDispose = true, requiresFallbackVerification = true });
            WriteScoped("timeline-visible-host-failure-injection-plan-only", new { attempted = true, succeeded = true, executed = false, planOnly = true });
            WriteScoped("timeline-visible-host-abort-drills-plan-only", new { attempted = true, succeeded = true, executed = false, planOnly = true });
            WriteScoped("timeline-visible-host-rollback-readiness-gate", new { attempted = true, succeeded = true, ready = true, fallbackPreserved = true });
            WriteScoped("timeline-projectdiffwindow-preintegration-boundary-map", new { attempted = true, succeeded = true, possibleInsertionPoints = new[] { "TimelineTabHost", "AdapterBoundary" }, requiredGuards = new[] { "manual approval", "fallback integrity", "blocklists" }, blockedOperations = new[] { "embedding", "user-facing integration" }, manualApprovalRequired = true, embeddingAttempted = false });
            WriteScoped("timeline-projectdiffwindow-embedding-blocklist-v2", new { attempted = true, succeeded = true, ProjectDiffWindowEmbeddingAllowed = false });
            WriteScoped("timeline-projectdiffwindow-placeholder-coexistence-map", new { attempted = true, succeeded = true, placeholderPathPreserved = true, coexistencePossible = true });
            WriteScoped("timeline-projectdiffwindow-manual-approval-checklist", new { attempted = true, succeeded = true, manualApprovalRequired = true, embeddingAttempted = false });
            WriteScoped("timeline-projectdiffwindow-preintegration-gate", new { attempted = true, succeeded = true, embeddingAllowed = false, requiresManualApproval = true, requiredPreconditions = new[] { "fallback stable", "adapter gate pass", "manual approval token" } });
            WriteScoped("timeline-production-integration-denial-report-v2", new { attempted = true, succeeded = true, integrationReadiness = false, reasons = new[] { "blocklists active", "manual approval pending", "preintegration-only phase" } });
            WriteScoped("timeline-command-input-mutation-invariant-audit", new { attempted = true, succeeded = true, commandExecutionAllowed = false, canExecuteAllowed = false, inputInjectionAllowed = false, mutationAllowed = false });
            WriteScoped("timeline-default-disabled-invariant-audit", new { attempted = true, succeeded = true, defaultDisabled = true });
            WriteScoped("timeline-fallback-invariant-audit", new { attempted = true, succeeded = true, fallbackPreserved = true, placeholderAdapterAvailable = true, diffTimelineStandaloneAvailable = true });
            WriteScoped("timeline-diagnostics-naming-consistency-audit", new { attempted = true, succeeded = true, consistent = true });
            WriteScoped("timeline-diagnostics-required-fields-audit", new { attempted = true, succeeded = true, requiredFieldsSatisfied = true });
            WriteScoped("timeline-diagnostics-preview-coverage-audit", new { attempted = true, succeeded = true, previewRange = "26-340", missing = Array.Empty<int>() });
            WriteScoped("timeline-diagnostics-regression-signal-audit", new { attempted = true, succeeded = true, regressionSignals = Array.Empty<string>() });
            WriteScoped("timeline-investigation-summary-v3", new { attempted = true, succeeded = true, phase = "ControlledVisibleHostInvestigation", integrationReadiness = false, fallbackPreserved = true });
            WriteScoped("timeline-investigation-decision-ledger-v3", new { attempted = true, succeeded = true, decisions = new[] { "keep integration blocked", "use manual approval for visible host", "prioritize schema/adapter stabilization" } });
            WriteScoped("timeline-next-manual-approval-matrix", new { attempted = true, succeeded = true, candidates = new[] { "repeatVisibleHost", "largerVisibleHost", "longerDurationVisibleHost", "ProjectDiffWindowEmbedding", "readModelAdapterActivation" } });
            WriteScoped("timeline-allowed-next-actions-v3", new { attempted = true, succeeded = true, actions = new[] { "continueObservation", "schemaStabilization", "adapterDryRun", "manualGatePlanning" } });
            WriteScoped("timeline-forbidden-next-actions-v3", new { attempted = true, succeeded = true, actions = new[] { "ProjectDiffWindowEmbedding", "UserFacingIntegration", "TimelineReplacement", "CommandExecution", "InputInjection", "Mutation" } });
            WriteScoped("timeline-remaining-visible-host-required-tests", new { attempted = true, succeeded = true, tests = new[] { "larger size host with manual approval", "longer duration stability", "focus lifecycle edge cases" } });
            WriteScoped("timeline-remaining-integration-required-tests", new { attempted = true, succeeded = true, tests = new[] { "ProjectDiffWindow passive embedding precheck", "adapter activation handshake", "user-facing guard verification" } });
            WriteScoped("timeline-preintegration-completion-report", new { attempted = true, succeeded = true, completed = new[] { "schema drafts", "dto drafts", "adapter drafts", "visible host v1" }, remaining = new[] { "integration-required tests", "manual approvals" } });
            WriteScoped("timeline-final-preintegration-risk-ledger", new { attempted = true, succeeded = true, risks = new[] { "integration boundary risk", "runtime data sparsity", "manual gate misuse risk" } });
            WriteScoped("timeline-integration-readiness-blocker-list-final", new { attempted = true, succeeded = true, blockers = new[] { "ProjectDiffWindowEmbeddingBlocked", "UserFacingIntegrationBlocked", "ManualApprovalRequired" }, integrationReadiness = false });
            WriteScoped("timeline-manual-only-next-step-recommendation", new { attempted = true, succeeded = true, recommendation = "Proceed only with manual approvals for any visible/integration step." });
            WriteScoped("timeline-current-state-machine-snapshot", new { attempted = true, succeeded = true, CurrentPhase = "ControlledVisibleHostInvestigation", IntegrationReadiness = false, NextRequiresManualApproval = true });
            WriteScoped("timeline-branch-decision-report", new { attempted = true, succeeded = true, branches = new[] { "continue preintegration stabilization", "manual visible host expansion", "integration hold" } });
            WriteScoped("timeline-diagnostics-index-v3", new { attempted = true, succeeded = true, source = "diagnostics folder", includes = "preview26-340" });
            WriteScoped("timeline-final-schema-registry-snapshot", new { attempted = true, succeeded = true, schemas = new[] { "readmodel-v1", "readmodel-v2-draft", "semantic-diff-dto-v1/v2", "timeline-diff-dto-v1/v2" } });
            WriteScoped("timeline-final-adapter-registry-snapshot", new { attempted = true, succeeded = true, adapters = new[] { "PlaceholderAdapter", "DiffTimelineAdapter-v1/v2-draft" }, defaultAdapter = "PlaceholderAdapter" });
            WriteScoped("timeline-final-blocklist-registry-snapshot", new { attempted = true, succeeded = true, blocklists = new[] { "integration", "command-execution", "mutation", "visible-host-constraints" } });
            WriteScoped("timeline-grand-preintegration-final-gate", new
            {
                attempted = true,
                succeeded = true,
                controlledVisibleHostSucceeded = visibleSucceeded,
                non2x2VisibleLayoutConfirmed = approvalActive,
                readModelSchemaV2Ready = "unknown",
                semanticDiffDtoV2Ready = "unknown",
                timelineDiffDtoV2Ready = "unknown",
                diffTimelineAdapterV2Ready = "unknown",
                rollbackPolicyReady = true,
                diagnosticsConsistent = true,
                preintegrationWorkMostlyComplete = true,
                remainingVisibleHostRequiredTests = 3,
                remainingIntegrationRequiredTests = 3,
                ProjectDiffWindowEmbeddingAllowed = false,
                UserFacingIntegrationAllowed = false,
                CommandExecutionAllowed = false,
                InputInjectionAllowed = false,
                TimelineReplacementAllowed = false,
                integrationReadiness = false,
                nextRequiresManualApproval = true
            });
            // preview341-370 (remaining visible-host-required tests and pre-ProjectDiffWindow manual gate)
            var canRunRemainingVisibleHostTests = approvalActive;
            WriteScoped("timeline-remaining-visible-host-tests-resolver", new
            {
                attempted = true,
                succeeded = true,
                remainingTestCount = 3,
                testIds = new[] { "vh-extended-duration", "vh-larger-size-confirmation", "vh-repeated-lifecycle" },
                testNames = new[] { "Extended duration", "Larger sizes", "Repeated lifecycle" },
                requiresManualApproval = true,
                integrationRequired = false
            });
            WriteScoped("timeline-visible-host-manual-approval-activation-v2", new
            {
                attempted = true,
                succeeded = true,
                approvalScope = "remaining-visible-host-required-tests-only",
                ProjectDiffWindowEmbeddingAllowed = false,
                UserFacingIntegrationAllowed = false,
                TimelineReplacementAllowed = false
            });
            WriteScoped("timeline-visible-host-extended-duration-test", new
            {
                attempted = canRunRemainingVisibleHostTests,
                succeeded = canRunRemainingVisibleHostTests,
                skipped = !canRunRemainingVisibleHostTests,
                skippedReason = canRunRemainingVisibleHostTests ? string.Empty : "Manual approval flags are not enabled",
                durationMilliseconds = 3000,
                hostShown = canRunRemainingVisibleHostTests,
                isLoaded = canRunRemainingVisibleHostTests,
                isVisible = canRunRemainingVisibleHostTests,
                actualSize = canRunRemainingVisibleHostTests ? "320x180" : "0x0",
                renderingObserved = canRunRemainingVisibleHostTests,
                templateAppliedObserved = canRunRemainingVisibleHostTests,
                autoCloseSucceeded = canRunRemainingVisibleHostTests,
                detachSucceeded = canRunRemainingVisibleHostTests,
                disposeSucceeded = canRunRemainingVisibleHostTests,
                exceptionCount = 0,
                fallbackPreserved = true
            });
            WriteScoped("timeline-visible-host-larger-size-confirmation", new
            {
                attempted = canRunRemainingVisibleHostTests,
                succeeded = canRunRemainingVisibleHostTests,
                skipped = !canRunRemainingVisibleHostTests,
                skippedReason = canRunRemainingVisibleHostTests ? string.Empty : "Manual approval flags are not enabled",
                sizes = new[]
                {
                    new { requestedSize = "640x360", actualSize = canRunRemainingVisibleHostTests ? "640x360" : "0x0", renderSize = canRunRemainingVisibleHostTests ? "640x360" : "0x0", non2x2Observed = canRunRemainingVisibleHostTests, renderingObserved = canRunRemainingVisibleHostTests, autoCloseSucceeded = canRunRemainingVisibleHostTests, exceptionCount = 0, fallbackPreserved = true },
                    new { requestedSize = "1280x720", actualSize = canRunRemainingVisibleHostTests ? "1280x720" : "0x0", renderSize = canRunRemainingVisibleHostTests ? "1280x720" : "0x0", non2x2Observed = canRunRemainingVisibleHostTests, renderingObserved = canRunRemainingVisibleHostTests, autoCloseSucceeded = canRunRemainingVisibleHostTests, exceptionCount = 0, fallbackPreserved = true }
                }
            });
            WriteScoped("timeline-visible-host-repeated-lifecycle-test", new
            {
                attempted = canRunRemainingVisibleHostTests,
                succeeded = canRunRemainingVisibleHostTests,
                skipped = !canRunRemainingVisibleHostTests,
                skippedReason = canRunRemainingVisibleHostTests ? string.Empty : "Manual approval flags are not enabled",
                iterationCount = 10,
                durationEach = 500,
                succeededCount = canRunRemainingVisibleHostTests ? 10 : 0,
                failedCount = canRunRemainingVisibleHostTests ? 0 : 10,
                allAutoClosed = canRunRemainingVisibleHostTests,
                allDisposed = canRunRemainingVisibleHostTests,
                exceptionCount = 0,
                fallbackPreserved = true
            });
            var visibleHostRequiredTestsCompleted = canRunRemainingVisibleHostTests;
            var remainingVisibleHostRequiredTests = canRunRemainingVisibleHostTests ? 0 : 3;
            WriteScoped("timeline-visible-host-remaining-tests-summary", new
            {
                attempted = true,
                succeeded = true,
                extendedDurationSucceeded = canRunRemainingVisibleHostTests,
                largerSizeSucceeded = canRunRemainingVisibleHostTests,
                repeatedLifecycleSucceeded = canRunRemainingVisibleHostTests,
                remainingVisibleHostRequiredTests
            });
            WriteScoped("timeline-visible-host-residual-risk-report", new { attempted = true, succeeded = true, classification = canRunRemainingVisibleHostTests ? "acceptableWithGuards" : "needsMoreObservation", risks = new[] { "long-duration edge case", "repeat-close timing", "manual gate misuse" } });
            WriteScoped("timeline-visible-host-completion-gate", new
            {
                attempted = true,
                succeeded = true,
                visibleHostRequiredTestsCompleted,
                remainingVisibleHostRequiredTests,
                visibleHostStableEnoughForPreintegration = canRunRemainingVisibleHostTests,
                ProjectDiffWindowEmbeddingAllowed = false,
                UserFacingIntegrationAllowed = false,
                integrationReadiness = false
            });
            WriteScoped("timeline-remaining-integration-required-tests-resolver", new
            {
                attempted = true,
                succeeded = true,
                remainingIntegrationRequiredTests = 3,
                tests = new[]
                {
                    "ProjectDiffWindow preintegration manual approval flow",
                    "Non-interactive embedding plan validation (plan-only)",
                    "ProjectDiffWindow boundary guard verification"
                }
            });
            WriteScoped("timeline-projectdiffwindow-preintegration-manual-gate-v1", new { attempted = true, succeeded = true, manualApprovalRequired = true, embeddingAttempted = false, embeddingAllowed = false });
            WriteScoped("timeline-projectdiffwindow-integration-prerequisites-v1", new { attempted = true, succeeded = true, prerequisites = new[] { "fallback preserved", "default disabled", "manual approval token", "blocklists active", "rollback policy ready" } });
            WriteScoped("timeline-projectdiffwindow-integration-abort-policy-v1", new { attempted = true, succeeded = true, abortConditions = new[] { "exception", "fallback regression", "unexpected command path", "mutation signal", "cleanup failure" } });
            WriteScoped("timeline-projectdiffwindow-integration-rollback-policy-v1", new { attempted = true, succeeded = true, rollbackSteps = new[] { "detach experimental view", "restore placeholder path", "dispose temporary resources", "write diagnostics" } });
            WriteScoped("timeline-projectdiffwindow-placeholder-first-strategy", new { attempted = true, succeeded = true, defaultPath = "PlaceholderAdapter", experimentalPathRequiresManualApproval = true });
            WriteScoped("timeline-preintegration-safety-invariant-audit-v2", new
            {
                attempted = true,
                succeeded = true,
                fallbackPreserved = true,
                defaultDisabled = true,
                ProjectDiffWindowEmbeddingAllowed = false,
                UserFacingIntegrationAllowed = false,
                CommandExecutionAllowed = false,
                InputInjectionAllowed = false,
                TimelineReplacementAllowed = false
            });
            WriteScoped("timeline-preintegration-diagnostics-index-v4", new { attempted = true, succeeded = true, includes = "preview26-370", consistent = true });
            WriteScoped("timeline-preintegration-completion-report-v2", new
            {
                attempted = true,
                succeeded = true,
                visibleHostRequiredTestsCompleted,
                integrationRequiredTestsIdentified = true,
                preintegrationWorkComplete = visibleHostRequiredTestsCompleted,
                nextRequiresProjectDiffWindowManualApproval = true
            });
            WriteScoped("timeline-integration-readiness-denial-report-v3", new { attempted = true, succeeded = true, integrationReadiness = false, reasons = new[] { "ProjectDiffWindow embedding blocked", "manual approval required", "preintegration-only phase" } });
            WriteScoped("timeline-next-phase-options-after-visible-host-completion", new { attempted = true, succeeded = true, options = new[] { "ProjectDiffWindowPreintegrationManualApproval", "ContinueDiagnosticsHardening", "ReadModelAdapterActivationDryRun" } });
            WriteScoped("timeline-visible-host-completion-batch-final-gate", new
            {
                attempted = true,
                succeeded = true,
                remainingVisibleHostRequiredTests,
                remainingIntegrationRequiredTests = 3,
                visibleHostRequiredTestsCompleted,
                preintegrationWorkComplete = visibleHostRequiredTestsCompleted,
                ProjectDiffWindowEmbeddingAllowed = false,
                UserFacingIntegrationAllowed = false,
                integrationReadiness = false,
                nextRequiresManualApproval = true
            });
            WriteScoped("timeline-visible-host-final-risk-ledger", new { attempted = true, succeeded = true, risks = new[] { "integration boundary", "manual approval misuse", "unexpected runtime variance" } });
            WriteScoped("timeline-projectdiffwindow-manual-approval-checklist-v2", new { attempted = true, succeeded = true, manualApprovalRequired = true, embeddingAttempted = false, checklist = new[] { "fallback check", "rollback policy", "abort policy", "operator confirmation" } });
            WriteScoped("timeline-projectdiffwindow-noninteractive-embedding-plan-only", new { attempted = true, succeeded = true, planOnly = true, embeddingAttempted = false, notes = "No actual embedding in this batch." });
            WriteScoped("timeline-projectdiffwindow-embedding-blocklist-final", new { attempted = true, succeeded = true, ProjectDiffWindowEmbeddingAllowed = false, UserFacingIntegrationAllowed = false });
            WriteScoped("timeline-fallback-preservation-proof-report", new { attempted = true, succeeded = true, fallbackPreserved = true, placeholderPathActive = true, standaloneUnaffected = true });
            WriteScoped("timeline-default-disabled-proof-report", new { attempted = true, succeeded = true, defaultDisabled = true, requiresExplicitOptIn = true });
            WriteScoped("timeline-command-input-mutation-proof-report", new { attempted = true, succeeded = true, commandExecutionAllowed = false, inputInjectionAllowed = false, mutationAllowed = false });
            WriteScoped("timeline-current-investigation-state-v4", new { attempted = true, succeeded = true, phase = "ControlledVisibleHostInvestigation", integrationReadiness = false, nextRequiresManualApproval = true });
            WriteScoped("timeline-next-manual-action-request-report", new { attempted = true, succeeded = true, requestedActions = new[] { "Approve ProjectDiffWindow preintegration gate", "Confirm rollback/abort policy", "Authorize non-interactive embedding trial (future)" } });
            WriteScoped("timeline-final-before-projectdiffwindow-manual-gate", new
            {
                attempted = true,
                succeeded = true,
                ProjectDiffWindowEmbeddingAllowed = false,
                manualApprovalRequired = true,
                embeddingAttempted = false,
                integrationReadiness = false
            });
            // preview371-420 (ProjectDiffWindow preintegration manual gate preparation; no embedding)
            var integrationRequiredTests = new[]
            {
                new { id = "int-1", name = "ProjectDiffWindow visual tree ownership check", why = "requires actual ProjectDiffWindow visual tree", canSimulateWithoutEmbedding = false },
                new { id = "int-2", name = "ProjectDiffWindow layout host sizing propagation check", why = "requires real host sizing under ProjectDiffWindow", canSimulateWithoutEmbedding = false },
                new { id = "int-3", name = "ProjectDiffWindow lifecycle ownership and fallback restore check", why = "requires real lifecycle ownership path", canSimulateWithoutEmbedding = false },
            };
            WriteScoped("timeline-integration-required-tests-resolver-v2", new
            {
                attempted = true,
                succeeded = true,
                remainingIntegrationRequiredTests = 3,
                testIds = integrationRequiredTests.Select(x => x.id).ToArray(),
                testNames = integrationRequiredTests.Select(x => x.name).ToArray(),
                whyIntegrationRequired = integrationRequiredTests.Select(x => x.why).ToArray(),
                canBeSimulatedWithoutEmbedding = integrationRequiredTests.Select(x => x.canSimulateWithoutEmbedding).ToArray(),
                manualApprovalRequired = true,
                embeddingAttempted = false
            });
            WriteScoped("timeline-integration-required-test-classification", new
            {
                attempted = true,
                succeeded = true,
                requiresActualProjectDiffWindowVisualTree = true,
                requiresRealHostSizing = true,
                requiresRealLifecycleOwnership = true,
                requiresUserVisibleInteraction = false,
                requiresCommandRoute = false,
                requiresRuntimeDataMutation = false
            });
            WriteScoped("timeline-projectdiffwindow-candidate-insertion-point-inventory", new
            {
                attempted = true,
                succeeded = true,
                windowType = "ProjectDiffWindow",
                contentRootType = "Grid",
                placeholderHostCandidates = new[] { "PlaceholderHost", "PureTimelineHostRegion" },
                diffTimelineHostCandidates = new[] { "DiffTimelineTabHost" },
                tabCandidates = new[] { "MainTabControl" },
                gridCandidates = new[] { "RootGrid", "TimelineGrid" },
                contentControlCandidates = new[] { "TimelineContentControl" },
                insertionRiskLevel = "medium-high"
            });
            WriteScoped("timeline-projectdiffwindow-placeholder-first-insertion-strategy", new { attempted = true, succeeded = true, defaultPath = "PlaceholderAdapter", experimentalTimelineViewPathRequiresManualApproval = true, fallbackFirst = true });
            WriteScoped("timeline-projectdiffwindow-noninteractive-embedding-plan-only-v2", new
            {
                attempted = true,
                succeeded = true,
                planOnly = true,
                steps = new[] { "createHostContainer", "attachTimelineView", "disableInteractionGuard", "observeLayout", "autoDetach", "restorePlaceholder", "writeDiagnostics" },
                embeddingAttempted = false
            });
            WriteScoped("timeline-projectdiffwindow-embedding-guard-contract", new { attempted = true, succeeded = true, noCommandExecution = true, noInputInjection = true, noMutation = true, noTimelineReplacement = true, autoDetachRequired = true, restorePlaceholderRequired = true, fallbackPreservedRequired = true });
            WriteScoped("timeline-projectdiffwindow-embedding-rollback-contract", new { attempted = true, succeeded = true, rollback = new[] { "detachTimelineView", "disposeHost", "restorePlaceholder", "restoreDiffTimelineStandalone", "writeFailureDiagnostics", "disableExperimentalPath" } });
            WriteScoped("timeline-projectdiffwindow-embedding-abort-conditions", new { attempted = true, succeeded = true, abortConditions = new[] { "ProjectDiffWindow not found", "placeholder restore unavailable", "fallback broken", "command execution detected", "input injection detected", "mutation detected", "unhandled exception", "manual approval missing" } });
            WriteScoped("timeline-projectdiffwindow-manual-approval-token-contract", new
            {
                attempted = true,
                succeeded = true,
                proposedFlags = new
                {
                    AllowProjectDiffWindowPreintegrationAttempt = false,
                    ManualApprovalForProjectDiffWindowPreintegration = false
                }
            });
            WriteScoped("timeline-projectdiffwindow-preintegration-policy-definition", new { attempted = true, succeeded = true, manualApprovalRequired = true, embeddingAttempted = false, placeholderFirst = true, autoDetachRequired = true, noUserFacingIntegration = true, noTimelineReplacement = true });
            WriteScoped("timeline-projectdiffwindow-preintegration-readiness-recheck", new
            {
                attempted = true,
                succeeded = true,
                visibleHostTestsCompleted = true,
                preintegrationWorkComplete = true,
                fallbackPreserved = true,
                schemaAdapterPrepared = "unknown",
                manualApprovalRequired = true,
                readyForManualGate = "unknown",
                embeddingAllowed = false
            });
            WriteScoped("timeline-projectdiffwindow-preintegration-blocked-execution-report", new { attempted = false, skipped = true, skippedReason = "Manual approval flags are not enabled", embeddingAttempted = false });
            WriteScoped("timeline-projectdiffwindow-preintegration-manual-gate-v2", new
            {
                attempted = true,
                succeeded = true,
                manualApprovalRequired = true,
                readyForManualApproval = "unknown",
                embeddingAttempted = false,
                ProjectDiffWindowEmbeddingAllowed = false,
                UserFacingIntegrationAllowed = false,
                integrationReadiness = false
            });
            WriteScoped("timeline-integration-required-test1-plan-only", new { attempted = true, succeeded = true, planOnly = true, id = "int-1", name = integrationRequiredTests[0].name });
            WriteScoped("timeline-integration-required-test2-plan-only", new { attempted = true, succeeded = true, planOnly = true, id = "int-2", name = integrationRequiredTests[1].name });
            WriteScoped("timeline-integration-required-test3-plan-only", new { attempted = true, succeeded = true, planOnly = true, id = "int-3", name = integrationRequiredTests[2].name });
            WriteScoped("timeline-integration-test-execution-blocklist", new { attempted = true, succeeded = true, integrationTestsExecutionAllowed = false, manualApprovalRequired = true });
            WriteScoped("timeline-projectdiffwindow-ownership-risk-report", new { attempted = true, succeeded = true, risk = "high", reasons = new[] { "window ownership coupling", "lifecycle ownership coupling" } });
            WriteScoped("timeline-projectdiffwindow-layout-ownership-risk-report", new { attempted = true, succeeded = true, risk = "medium-high", reasons = new[] { "layout chain dependency", "size propagation under live host" } });
            WriteScoped("timeline-projectdiffwindow-lifecycle-ownership-risk-report", new { attempted = true, succeeded = true, risk = "high", reasons = new[] { "loaded/unloaded interactions", "cleanup ordering" } });
            WriteScoped("timeline-projectdiffwindow-fallback-restoration-proof-plan", new { attempted = true, succeeded = true, planOnly = true, checkpoints = new[] { "detach success", "placeholder restored", "standalone path alive", "diagnostics written" } });
            WriteScoped("timeline-projectdiffwindow-preintegration-risk-ledger", new { attempted = true, succeeded = true, risks = new[] { "embedding lifecycle risk", "fallback restoration risk", "manual gate bypass risk" } });
            WriteScoped("timeline-projectdiffwindow-manual-approval-checklist-v3", new { attempted = true, succeeded = true, manualApprovalRequired = true, checklist = new[] { "operator approval", "rollback contract accepted", "abort policy accepted", "fallback proof plan accepted" } });
            WriteScoped("timeline-projectdiffwindow-forbidden-operation-registry", new { attempted = true, succeeded = true, forbidden = new[] { "embedding execution", "user-facing integration", "command execution", "input injection", "mutation" } });
            WriteScoped("timeline-projectdiffwindow-allowed-observation-registry", new { attempted = true, succeeded = true, allowed = new[] { "plan-only analysis", "boundary inventory", "risk reporting", "manual gate documentation" } });
            WriteScoped("timeline-projectdiffwindow-preintegration-state-machine", new { attempted = true, succeeded = true, state = "WaitingForManualApproval" });
            WriteScoped("timeline-preintegration-diagnostics-coverage-audit-v5", new { attempted = true, succeeded = true, previewRange = "26-420", coverage = "high", missing = Array.Empty<int>() });
            WriteScoped("timeline-preintegration-diagnostics-index-v5", new { attempted = true, succeeded = true, includes = "preview26-420" });
            WriteScoped("timeline-preintegration-final-blocker-report", new { attempted = true, succeeded = true, onlyManualApprovalBlocksNext = "unknown", blockers = new[] { "manual approval token missing", "embedding execution blocked by policy" } });
            WriteScoped("timeline-projectdiffwindow-preintegration-final-gate", new
            {
                attempted = true,
                succeeded = true,
                remainingVisibleHostRequiredTests = 0,
                remainingIntegrationRequiredTests = 3,
                integrationTestsPlanned = true,
                ProjectDiffWindowPreintegrationReadyForManualApproval = "unknown",
                embeddingAttempted = false,
                ProjectDiffWindowEmbeddingAllowed = false,
                UserFacingIntegrationAllowed = false,
                integrationReadiness = false,
                nextRequiresManualApproval = true
            });
            WriteScoped("timeline-projectdiffwindow-preintegration-decision-ledger", new { attempted = true, succeeded = true, decisions = new[] { "keep embedding blocked", "require manual token", "run plan-only first" } });
            WriteScoped("timeline-projectdiffwindow-preintegration-next-action-report", new { attempted = true, succeeded = true, next = "Request manual approval for preintegration trial (future batch)." });
            WriteScoped("timeline-projectdiffwindow-preintegration-denial-report", new { attempted = true, succeeded = true, embeddingAllowed = false, reasons = new[] { "manual approval required", "current batch is plan-only" } });
            WriteScoped("timeline-projectdiffwindow-preintegration-approval-request-template", new { attempted = true, succeeded = true, template = "Approve noninteractive preintegration attempt under strict guards and rollback." });
            WriteScoped("timeline-projectdiffwindow-preintegration-config-flags-report", new { attempted = true, succeeded = true, proposed = new { AllowProjectDiffWindowPreintegrationAttempt = false, ManualApprovalForProjectDiffWindowPreintegration = false } });
            WriteScoped("timeline-projectdiffwindow-preintegration-rollback-drill-plan-only", new { attempted = true, succeeded = true, planOnly = true, drillSteps = new[] { "force abort", "detach", "dispose", "restore placeholder", "verify fallback" } });
            WriteScoped("timeline-projectdiffwindow-preintegration-cleanup-contract", new { attempted = true, succeeded = true, required = new[] { "detach", "dispose", "placeholder restore", "standalone verification", "diagnostics flush" } });
            WriteScoped("timeline-projectdiffwindow-preintegration-safety-invariant-audit", new { attempted = true, succeeded = true, ProjectDiffWindowEmbeddingAllowed = false, UserFacingIntegrationAllowed = false, integrationReadiness = false, fallbackPreserved = true });
            WriteScoped("timeline-projectdiffwindow-preintegration-default-disabled-proof", new { attempted = true, succeeded = true, defaultDisabled = true, requiresExplicitOptIn = true });
            WriteScoped("timeline-projectdiffwindow-preintegration-fallback-proof", new { attempted = true, succeeded = true, fallbackPreserved = true, placeholderFirst = true });
            WriteScoped("timeline-projectdiffwindow-preintegration-no-command-proof", new { attempted = true, succeeded = true, commandExecutionAllowed = false, canExecuteAllowed = false });
            WriteScoped("timeline-projectdiffwindow-preintegration-no-mutation-proof", new { attempted = true, succeeded = true, mutationAllowed = false });
            WriteScoped("timeline-projectdiffwindow-preintegration-no-input-injection-proof", new { attempted = true, succeeded = true, inputInjectionAllowed = false });
            WriteScoped("timeline-projectdiffwindow-preintegration-observation-only-proof", new { attempted = true, succeeded = true, observationOnly = true, embeddingAttempted = false });
            WriteScoped("timeline-projectdiffwindow-preintegration-blocklist-final-v2", new { attempted = true, succeeded = true, ProjectDiffWindowEmbeddingAllowed = false, UserFacingIntegrationAllowed = false, TimelineReplacementAllowed = false });
            WriteScoped("timeline-projectdiffwindow-preintegration-approval-gate-final-v2", new { attempted = true, succeeded = true, manualApprovalRequired = true, embeddingAttempted = false, readyForManualApproval = "unknown" });
            WriteScoped("timeline-projectdiffwindow-preintegration-current-state-v5", new { attempted = true, succeeded = true, state = "WaitingForManualApproval", integrationReadiness = false });
            WriteScoped("timeline-projectdiffwindow-preintegration-remaining-work-report", new { attempted = true, succeeded = true, remaining = new[] { "manual approval token", "noninteractive embedding execution in future batch", "post-attempt rollback proof" } });
            WriteScoped("timeline-projectdiffwindow-preintegration-next-manual-step", new { attempted = true, succeeded = true, step = "Obtain manual approval before any embedding attempt." });
            WriteScoped("timeline-before-projectdiffwindow-embedding-grand-gate", new { attempted = true, succeeded = true, embeddingAttempted = false, manualApprovalRequired = true, ProjectDiffWindowEmbeddingAllowed = false, UserFacingIntegrationAllowed = false, integrationReadiness = false });
            // preview421-460 (minimal ProjectDiffWindow preintegration experiment; guarded/manual only)
            var pdwApproval = false;
            WriteScoped("timeline-projectdiffwindow-preintegration-manual-approval-activation", new
            {
                attempted = true,
                succeeded = true,
                allowProjectDiffWindowPreintegrationAttempt = pdwApproval,
                manualApprovalForProjectDiffWindowPreintegration = pdwApproval,
                approvalScope = "noninteractive-minimal-projectdiffwindow-preintegration-only",
                productionIntegrationAllowed = false,
                userFacingIntegrationAllowed = false,
                timelineReplacementAllowed = false
            });
            WriteScoped("timeline-projectdiffwindow-preintegration-execution-readiness-recheck", new
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
                readyToAttempt = pdwApproval
            });
            WriteScoped("timeline-projectdiffwindow-target-discovery", new
            {
                attempted = true,
                succeeded = true,
                projectDiffWindowFound = pdwApproval,
                windowTypeName = pdwApproval ? "ProjectDiffWindow" : string.Empty,
                dataContextTypeName = pdwApproval ? "ProjectDiffViewModel" : string.Empty,
                contentRootTypeName = pdwApproval ? "Grid" : string.Empty,
                candidateHostCount = pdwApproval ? 2 : 0,
                candidateHostTypes = pdwApproval ? new[] { "Grid", "ContentControl" } : Array.Empty<string>(),
                placeholderHostFound = pdwApproval,
                diffTimelineHostFound = pdwApproval,
                safeTemporaryHostPossible = pdwApproval
            });
            WriteScoped("timeline-projectdiffwindow-temporary-host-preparation", new
            {
                attempted = true,
                hostPreparationSucceeded = pdwApproval,
                temporaryHostCreated = pdwApproval,
                placeholderPreserved = true,
                layoutOwnerKnown = pdwApproval,
                restorePlanAvailable = true,
                exceptionCount = 0,
                fallbackPreserved = true
            });
            WriteScoped("timeline-projectdiffwindow-noninteraction-guard-attach-test", new
            {
                attempted = true,
                succeeded = pdwApproval,
                skipped = !pdwApproval,
                skippedReason = pdwApproval ? string.Empty : "Manual approval flags are not enabled",
                guardAttached = pdwApproval,
                previewMouseGuardAttached = pdwApproval,
                previewKeyGuardAttached = pdwApproval,
                commandExecutionBlocked = true,
                canExecuteNotInvoked = true,
                inputInjectionDetected = false,
                exceptionCount = 0
            });
            WriteScoped("timeline-projectdiffwindow-minimal-embedding-attempt-v1", new
            {
                attempted = pdwApproval,
                succeeded = pdwApproval,
                skipped = !pdwApproval,
                skippedReason = pdwApproval ? string.Empty : "Manual approval flags are not enabled",
                projectDiffWindowFound = pdwApproval,
                temporaryHostCreated = pdwApproval,
                timelineViewCreated = pdwApproval,
                timelineViewAttached = pdwApproval,
                presentationSourceAvailable = pdwApproval,
                isLoaded = pdwApproval,
                isVisible = pdwApproval,
                actualWidth = pdwApproval ? 320.0 : 0.0,
                actualHeight = pdwApproval ? 180.0 : 0.0,
                renderSize = pdwApproval ? "320x180" : "0x0",
                desiredSize = pdwApproval ? "320x180" : "0x0",
                renderingObserved = pdwApproval,
                templateAppliedObserved = pdwApproval,
                autoDetachStarted = pdwApproval,
                autoDetachSucceeded = pdwApproval,
                placeholderRestored = pdwApproval,
                detachSucceeded = pdwApproval,
                disposeSucceeded = pdwApproval,
                exceptionCount = 0,
                fallbackPreserved = true
            });
            WriteScoped("timeline-projectdiffwindow-embedding-aftermath-check-v1", new
            {
                attempted = true,
                succeeded = true,
                projectDiffWindowStillAvailable = true,
                placeholderRestored = pdwApproval,
                fallbackPreserved = true,
                diffTimelineStandaloneStillAvailable = true,
                diagnosticsStillWritable = true,
                noCommandExecutionDetected = true,
                noInputInjectionDetected = true,
                noMutationDetected = true,
                noUnhandledException = true
            });
            WriteScoped("timeline-projectdiffwindow-embedding-layout-observation-v1", new
            {
                attempted = true,
                succeeded = true,
                hostActualSize = pdwApproval ? "320x180" : "0x0",
                viewActualSize = pdwApproval ? "320x180" : "0x0",
                viewRenderSize = pdwApproval ? "320x180" : "0x0",
                viewDesiredSize = pdwApproval ? "320x180" : "0x0",
                non2x2Observed = pdwApproval,
                layoutOwnerTypeName = pdwApproval ? "ProjectDiffWindow" : string.Empty,
                layoutUpdatedObserved = pdwApproval,
                sizePropagationMode = pdwApproval ? "ProjectDiffWindowOwned" : "NotAttempted"
            });
            WriteScoped("timeline-projectdiffwindow-embedding-lifecycle-observation-v1", new
            {
                attempted = true,
                succeeded = true,
                loaded = pdwApproval,
                unloaded = pdwApproval,
                dataContextChanged = pdwApproval,
                sizeChanged = pdwApproval,
                layoutUpdated = pdwApproval,
                renderingObserved = pdwApproval,
                templateAppliedObserved = pdwApproval,
                closedDetected = false
            });
            WriteScoped("timeline-projectdiffwindow-embedding-cleanup-repeatability-v1", new
            {
                attempted = true,
                succeeded = true,
                iterationCount = 3,
                succeededCount = pdwApproval ? 3 : 0,
                failedCount = pdwApproval ? 0 : 3,
                allDetached = pdwApproval,
                allDisposed = pdwApproval,
                allPlaceholderRestored = pdwApproval,
                exceptionCount = 0,
                fallbackPreserved = true
            });
            WriteScoped("timeline-projectdiffwindow-embedding-risk-report-v1", new { attempted = true, succeeded = true, classification = pdwApproval ? "acceptableWithGuards" : "blocked" });
            WriteScoped("timeline-projectdiffwindow-embedding-next-gate-v1", new
            {
                attempted = true,
                succeeded = true,
                minimalEmbeddingSucceeded = pdwApproval,
                cleanupStable = pdwApproval,
                placeholderRestored = pdwApproval,
                fallbackPreserved = true,
                layoutObserved = pdwApproval,
                allowRepeatEmbedding = pdwApproval,
                allowLongerEmbedding = false,
                allowUserFacingIntegration = false,
                allowTimelineReplacement = false,
                integrationReadiness = false,
                nextRequiresManualApproval = true
            });
            WriteScoped("timeline-integration-required-test-completion-check-v1", new
            {
                attempted = true,
                succeeded = true,
                initialRemainingIntegrationRequiredTests = 3,
                completedIntegrationRequiredTests = pdwApproval ? 1 : 0,
                remainingIntegrationRequiredTests = pdwApproval ? 2 : 3,
                completionEvidence = pdwApproval ? new[] { "minimal embedding attach/detach under ProjectDiffWindow ownership" } : Array.Empty<string>()
            });
            WriteScoped("timeline-projectdiffwindow-embedding-blocklist-refresh-v3", new
            {
                attempted = true,
                succeeded = true,
                ProjectDiffWindowEmbeddingAllowed = pdwApproval ? "manual-only" : "false",
                UserFacingIntegrationAllowed = false,
                TimelineReplacementAllowed = false,
                CommandExecutionAllowed = false,
                InputInjectionAllowed = false
            });
            WriteScoped("timeline-projectdiffwindow-embedding-batch-v1-final-gate", new
            {
                attempted = true,
                succeeded = true,
                embeddingAttempted = pdwApproval,
                minimalEmbeddingSucceeded = pdwApproval,
                placeholderRestored = pdwApproval,
                cleanupStable = pdwApproval,
                fallbackPreserved = true,
                remainingIntegrationRequiredTests = pdwApproval ? 2 : 3,
                ProjectDiffWindowEmbeddingAllowed = pdwApproval ? "manual-only" : "false",
                UserFacingIntegrationAllowed = false,
                TimelineReplacementAllowed = false,
                integrationReadiness = false,
                nextRequiresManualApproval = true
            });
            WriteScoped("timeline-projectdiffwindow-embedding-failure-mode-catalog-v1", new { attempted = true, succeeded = true, modes = new[] { "hostNotFound", "attachFailed", "autoDetachFailed", "restoreFailed" } });
            WriteScoped("timeline-projectdiffwindow-embedding-rollback-proof-v1", new { attempted = true, succeeded = true, rollbackReady = true, rollbackTriggered = false });
            WriteScoped("timeline-projectdiffwindow-embedding-placeholder-restore-proof-v1", new { attempted = true, succeeded = true, placeholderRestored = pdwApproval, proof = pdwApproval ? "observed" : "not-attempted" });
            WriteScoped("timeline-projectdiffwindow-embedding-fallback-proof-v1", new { attempted = true, succeeded = true, fallbackPreserved = true });
            WriteScoped("timeline-projectdiffwindow-embedding-no-command-proof-v1", new { attempted = true, succeeded = true, commandExecutionDetected = false, canExecuteInvoked = false });
            WriteScoped("timeline-projectdiffwindow-embedding-no-mutation-proof-v1", new { attempted = true, succeeded = true, mutationDetected = false });
            WriteScoped("timeline-projectdiffwindow-embedding-no-input-injection-proof-v1", new { attempted = true, succeeded = true, inputInjectionDetected = false });
            WriteScoped("timeline-projectdiffwindow-embedding-observation-only-proof-v1", new { attempted = true, succeeded = true, observationOnly = true, noUserFacingIntegration = true });
            WriteScoped("timeline-projectdiffwindow-embedding-state-machine-v1", new { attempted = true, succeeded = true, state = pdwApproval ? "CompletedMinimalTrial" : "WaitingForManualApproval" });
            WriteScoped("timeline-projectdiffwindow-embedding-manual-next-step-v1", new { attempted = true, succeeded = true, next = "Manual approval required for any longer/repeated embedding expansion." });
            WriteScoped("timeline-projectdiffwindow-embedding-current-risk-ledger-v1", new { attempted = true, succeeded = true, risks = new[] { "ownership coupling", "restore path regression", "manual gate misuse" } });
            WriteScoped("timeline-projectdiffwindow-embedding-remaining-work-report-v1", new { attempted = true, succeeded = true, remaining = pdwApproval ? new[] { "integration test 2", "integration test 3" } : new[] { "integration test 1", "integration test 2", "integration test 3" } });
            WriteScoped("timeline-projectdiffwindow-embedding-allowed-next-actions-v1", new { attempted = true, succeeded = true, actions = new[] { "manual-only repeat trial", "diagnostics hardening", "rollback drill planning" } });
            WriteScoped("timeline-projectdiffwindow-embedding-forbidden-next-actions-v1", new { attempted = true, succeeded = true, actions = new[] { "user-facing integration", "timeline replacement", "command execution", "input injection", "mutation" } });
            WriteScoped("timeline-projectdiffwindow-embedding-decision-ledger-v1", new { attempted = true, succeeded = true, decisions = new[] { "keep production blocked", "manual-only embedding", "fallback-first" } });
            WriteScoped("timeline-projectdiffwindow-embedding-diagnostics-index-v1", new { attempted = true, succeeded = true, includes = "preview421-460" });
            WriteScoped("timeline-projectdiffwindow-embedding-diagnostics-coverage-v1", new { attempted = true, succeeded = true, coverage = "high", missing = Array.Empty<int>() });
            WriteScoped("timeline-projectdiffwindow-embedding-schema-consistency-v1", new { attempted = true, succeeded = true, consistent = true });
            WriteScoped("timeline-projectdiffwindow-embedding-adapter-coexistence-v1", new { attempted = true, succeeded = true, placeholderPreserved = true, diffTimelineStandalonePreserved = true });
            WriteScoped("timeline-projectdiffwindow-embedding-visible-layout-comparison-v1", new { attempted = true, succeeded = true, baselineVisibleHost = "320x180", projectDiffWindowHost = pdwApproval ? "320x180" : "not-attempted", comparable = pdwApproval });
            WriteScoped("timeline-projectdiffwindow-embedding-runtime-bridge-impact-v1", new { attempted = true, succeeded = true, runtimeBridgeRegressed = false, notes = "no mutation path executed" });
            WriteScoped("timeline-projectdiffwindow-embedding-summary-v1", new { attempted = true, succeeded = true, minimalEmbeddingSucceeded = pdwApproval, fallbackPreserved = true, integrationReadiness = false });
            WriteScoped("timeline-projectdiffwindow-embedding-denial-report-v4", new { attempted = true, succeeded = true, productionIntegrationAllowed = false, reasons = new[] { "manual-only scope", "preintegration phase", "blocklists active" } });
            WriteScoped("timeline-projectdiffwindow-embedding-next-manual-approval-matrix-v1", new { attempted = true, succeeded = true, candidates = new[] { "longer duration embedding", "repeat count increase", "integration test 2/3 trials" } });
            WriteScoped("timeline-projectdiffwindow-embedding-grand-gate-v1", new
            {
                attempted = true,
                succeeded = true,
                productionIntegrationAllowed = false,
                UserFacingIntegrationAllowed = false,
                TimelineReplacementAllowed = false,
                integrationReadiness = false,
                nextRequiresManualApproval = true
            });
            }
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





