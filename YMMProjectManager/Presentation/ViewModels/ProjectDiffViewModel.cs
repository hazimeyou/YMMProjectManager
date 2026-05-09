
using YMMProjectManager.Application.TimelineCore;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed class ProjectDiffViewModel : ViewModelBase, IDisposable
{
    private readonly DiffTimelineStandaloneConfig standaloneConfig = DiffTimelineStandaloneConfigResolver.ResolveFromEnvironment();
    private readonly InMemoryDiffTimelineSnapshotCache standalonePipelineCache = new();
    private DiffTimelineStandaloneValidationStatus? standaloneValidationStatus;
    private DiffTimelineRouteSelectionResult? standaloneRouteSelectionResult;
    private readonly FileLogger logger;
    private readonly ProjectSnapshotService snapshotService;
    private readonly JsonNormalizeService normalizeService;
    private readonly JsonDiffService jsonDiffService;
    private readonly YmmProjectDiffService ymmDiffService;
    private readonly RuntimeEnvironmentDetector runtimeEnvironmentDetector = new();
    private readonly PureTimelineExperimentalOptions pureTimelineExperimentalOptions = new();

    private string title = "差分";
    private string matchStatisticsText = string.Empty;
    private DiffEntryViewModel? selectedYmmDiffEntry;
    private bool isSyncingSelection;
    private string pureTimelineSelection = "(なし)";
    private string pureTimelineActiveScene = "(プレースホルダー)";
    private string pureTimelineActiveTimeline = "(プレースホルダー)";
    private TimelineSyncState selectedSyncState = TimelineSyncState.Detached;
    private TimelineMode selectedTimelineMode = TimelineMode.Synced;
    private string filterSearchText = string.Empty;
    private string selectedGroupingMode = "None";
    private string selectedChangeTypeFilter = "All";
    private string selectedSemanticCategoryFilter = "All";
    private string selectedPathFilter = string.Empty;
    private string selectedGroupFilter = string.Empty;
    private bool changedOnlyFilter;
    private bool warningOnlyFilter;
    private DiffTimelineFilteredResult? latestFilteredResult;
    private IReadOnlyList<DiffTimelineGroupState> latestGroupStates = [];
    private TimeSpan lastFilterDuration = TimeSpan.Zero;
    private readonly HashSet<string> selectedChangeTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> selectedSemanticCategories = new(StringComparer.Ordinal);
    private readonly HashSet<string> selectedPathFilters = new(StringComparer.Ordinal);
    private readonly HashSet<string> selectedGroupFilters = new(StringComparer.Ordinal);
    private DiffTimelineCoreResult? latestCoreResult;
    private DiffTimelineSnapshotListItem? selectedSnapshotListItem;
    private readonly List<DiffTimelineManualUiAction> manualUiActions = [];
    private string manualValidationSessionId = Guid.NewGuid().ToString("N");
    private string latestManualValidationLogPath = string.Empty;
    private string latestManualValidationSummary = string.Empty;
    private readonly DiffTimelineSnapshotRepository snapshotRepository;
    private readonly DiffTimelineComparisonHistoryStore comparisonHistoryStore;
    public DiffTimelineSnapshotBrowserViewModel SnapshotBrowser { get; } = new();
    public DiffTimelineSnapshotListItem? SelectedSnapshotListItem
    {
        get => selectedSnapshotListItem;
        set => SetProperty(ref selectedSnapshotListItem, value);
    }
    public string ManualValidationSessionId => manualValidationSessionId;
    public string LatestManualValidationLogPath
    {
        get => latestManualValidationLogPath;
        private set => SetProperty(ref latestManualValidationLogPath, value);
    }
    public string LatestManualValidationSummary
    {
        get => latestManualValidationSummary;
        private set => SetProperty(ref latestManualValidationSummary, value);
    }

    public ObservableCollection<DiffEntryViewModel> JsonDiffEntries { get; } = [];
    public ObservableCollection<DiffEntryViewModel> YmmDiffEntries { get; } = [];
    public ObservableCollection<DiffGroupViewModel> DiffGroups { get; } = [];
    public DiffTimelineViewModel TimelineViewModel { get; } = new();
    public PureTimelineHostViewModel PureTimelineHost { get; }

    public IReadOnlyList<SelectionOption<TimelineSyncState>> SyncStateOptions { get; } =
        Enum.GetValues<TimelineSyncState>().Select(x => new SelectionOption<TimelineSyncState>(x, ToSyncStateLabel(x))).ToList();
    public IReadOnlyList<SelectionOption<TimelineMode>> TimelineModeOptions { get; } =
        Enum.GetValues<TimelineMode>().Select(x => new SelectionOption<TimelineMode>(x, ToTimelineModeLabel(x))).ToList();
    public IReadOnlyList<SelectionOption<PureTimelineAdapterKind>> AdapterKindOptions { get; } =
        Enum.GetValues<PureTimelineAdapterKind>().Select(x => new SelectionOption<PureTimelineAdapterKind>(x, ToAdapterKindLabel(x))).ToList();

    public PureTimelineAdapterKind SelectedAdapterKind
    {
        get => PureTimelineHost.AdapterKind;
        set
        {
            if (PureTimelineHost.AdapterKind == value)
            {
                return;
            }

            PureTimelineHost.SwitchAdapter(value);
            OnPropertyChanged(nameof(SelectedAdapterKind));
            OnPropertyChanged(nameof(PureTimelineStatus));
            OnPropertyChanged(nameof(LastSyncAction));
        }
    }

    public string Title
    {
        get => title;
        private set => SetProperty(ref title, value);
    }

    public string MatchStatisticsText
    {
        get => matchStatisticsText;
        private set => SetProperty(ref matchStatisticsText, value);
    }

    public int PureTimelineCurrentFrame
    {
        get => PureTimelineHost.CurrentFrame;
        set
        {
            if (PureTimelineHost.CurrentFrame != Math.Max(0, value))
            {
                PureTimelineHost.CurrentFrame = Math.Max(0, value);
                TimelineViewModel.SetCurrentFrame(PureTimelineHost.CurrentFrame);
                OnPropertyChanged(nameof(PureTimelineCurrentFrame));
            }
        }
    }

    public string PureTimelineSelection
    {
        get => pureTimelineSelection;
        set => SetProperty(ref pureTimelineSelection, value);
    }

    public string PureTimelineStatus => ToPureTimelineStatusLabel(PureTimelineHost.Status);

    public string PureTimelineActiveScene
    {
        get => pureTimelineActiveScene;
        set => SetProperty(ref pureTimelineActiveScene, value);
    }

    public string PureTimelineActiveTimeline
    {
        get => pureTimelineActiveTimeline;
        set => SetProperty(ref pureTimelineActiveTimeline, value);
    }

    public string LastSyncAction
    {
        get => PureTimelineHost.LastAction;
    }

    private DiffTimelineStandaloneValidationStatus? StandaloneValidationStatus
    {
        get => standaloneValidationStatus;
        set => standaloneValidationStatus = value;
    }

    private DiffTimelineRouteSelectionResult? StandaloneRouteSelectionResult
    {
        get => standaloneRouteSelectionResult;
        set => standaloneRouteSelectionResult = value;
    }

    public TimelineSyncState SelectedSyncState
    {
        get => selectedSyncState;
        set
        {
            if (SetProperty(ref selectedSyncState, value))
            {
                ApplySyncModeAndState();
                TrySetHostFrame(PureTimelineCurrentFrame, "同期状態変更");
            }
        }
    }

    public TimelineMode SelectedTimelineMode
    {
        get => selectedTimelineMode;
        set
        {
            if (SetProperty(ref selectedTimelineMode, value))
            {
                ApplySyncModeAndState();
                TrySetHostFrame(PureTimelineCurrentFrame, "タイムラインモード変更");
            }
        }
    }

    public string PureTimelineSyncState => ToSyncStateLabel(TimelineViewModel.SyncState);
    public string PureTimelineMode => ToTimelineModeLabel(TimelineViewModel.Mode);
    public string RuntimeEnvironmentText => runtimeEnvironmentDetector.Detect().ToString();
    public string FilterSearchText { get => filterSearchText; set { if (SetProperty(ref filterSearchText, value)) ApplyStandaloneFiltersAndGrouping(); } }
    public bool ChangedOnlyFilter { get => changedOnlyFilter; set { if (SetProperty(ref changedOnlyFilter, value)) ApplyStandaloneFiltersAndGrouping(); } }
    public bool WarningOnlyFilter { get => warningOnlyFilter; set { if (SetProperty(ref warningOnlyFilter, value)) ApplyStandaloneFiltersAndGrouping(); } }
    public string SelectedGroupingMode { get => selectedGroupingMode; set { if (SetProperty(ref selectedGroupingMode, value)) ApplyStandaloneFiltersAndGrouping(); } }
    public string SelectedChangeTypeFilter { get => selectedChangeTypeFilter; set { if (SetProperty(ref selectedChangeTypeFilter, value)) { SetChangeTypeFilters(string.Equals(value, "All", StringComparison.Ordinal) ? [] : [value]); } } }
    public string SelectedSemanticCategoryFilter { get => selectedSemanticCategoryFilter; set { if (SetProperty(ref selectedSemanticCategoryFilter, value)) { SetSemanticCategoryFilters(string.Equals(value, "All", StringComparison.Ordinal) ? [] : [value]); } } }
    public string SelectedPathFilter { get => selectedPathFilter; set { if (SetProperty(ref selectedPathFilter, value)) { SetPathFilters(string.IsNullOrWhiteSpace(value) ? [] : [value]); } } }
    public string SelectedGroupFilter { get => selectedGroupFilter; set { if (SetProperty(ref selectedGroupFilter, value)) { selectedGroupFilters.Clear(); if (!string.IsNullOrWhiteSpace(value)) selectedGroupFilters.Add(value); ApplyStandaloneFiltersAndGrouping(); } } }
    public string LastFilterDiagnostics => latestFilteredResult is null
        ? "filter: none"
        : $"matched={latestFilteredResult.MatchedRowCount}, filteredOut={latestFilteredResult.FilteredOutCount}, ms={lastFilterDuration.TotalMilliseconds:F1}";
    public string ActiveFilterSummary => latestFilteredResult is null
        ? "active filters: none"
        : string.Join(" | ", latestFilteredResult.ActiveFilters.Where(x => !string.IsNullOrWhiteSpace(x.Value)).Select(x => $"{x.Key}={x.Value}"));
    public string NoMatchStateText => latestFilteredResult is null
        ? string.Empty
        : latestFilteredResult.MatchedRowCount == 0 ? "一致する差分がありません（フィルター条件を見直してください）" : string.Empty;
    public IReadOnlyList<string> GroupingModeOptions { get; } = ["None", "Semantic", "Timeline", "Layer", "Field", "Path", "ChangeType"];
    public IReadOnlyList<string> ChangeTypeFilterOptions { get; } = ["All", "追加", "削除", "変更", "移動"];
    public IReadOnlyList<string> SemanticFilterOptions { get; } = ["All", "Added", "Removed", "TimelinePosition", "Property", "Text"];

    public DiffEntryViewModel? SelectedYmmDiffEntry
    {
        get => selectedYmmDiffEntry;
        set
        {
            if (!SetProperty(ref selectedYmmDiffEntry, value))
            {
                return;
            }

            if (isSyncingSelection || value is null)
            {
                return;
            }

            isSyncingSelection = true;
            try
            {
                TimelineViewModel.SelectById(value.Id);
                PureTimelineCurrentFrame = value.Frame;
                PureTimelineSelection = value.Id;
            }
            finally
            {
                isSyncingSelection = false;
            }
        }
    }

    public ProjectDiffViewModel(
        FileLogger logger,
        ProjectSnapshotService snapshotService,
        JsonNormalizeService normalizeService,
        JsonDiffService jsonDiffService,
        YmmProjectDiffService ymmDiffService)
    {
        this.logger = logger;
        this.snapshotService = snapshotService;
        this.normalizeService = normalizeService;
        this.jsonDiffService = jsonDiffService;
        this.ymmDiffService = ymmDiffService;
        var diagnosticsRoot = Path.Combine(AppContext.BaseDirectory, "diagnostics");
        snapshotRepository = new DiffTimelineSnapshotRepository(diagnosticsRoot);
        comparisonHistoryStore = new DiffTimelineComparisonHistoryStore(diagnosticsRoot);
        PureTimelineHost = new PureTimelineHostViewModel(PureTimelineAdapterKind.Placeholder, pureTimelineExperimentalOptions);

        TimelineViewModel.SelectedDiffItemChanged += OnTimelineSelectedDiffItemChanged;
        ApplySyncModeAndState();
        TryInitializeHost();
        TryValidateStandalonePipeline();
    }

    public void SyncFrameFromPlaceholder()
    {
        TimelineViewModel.SetCurrentFrame(PureTimelineCurrentFrame);
        TrySetHostFrame(PureTimelineCurrentFrame, "フレーム同期");
    }

    public void GoToCurrentFrame()
    {
        TimelineViewModel.ScrollToCurrentFrame();
        TrySetHostFrame(PureTimelineCurrentFrame, "現在フレームへ移動");
    }

    public void CenterCurrentFrame()
    {
        TimelineViewModel.CenterCurrentFrame();
        TryCenterHostFrame(PureTimelineCurrentFrame);
    }

    public void SelectNearestDiffToCurrentFrame()
    {
        var ok = TimelineViewModel.SelectNearestDiffToCurrentFrame();
        if (ok)
        {
            TrySetHostFrame(PureTimelineCurrentFrame, "最寄り差分選択");
        }
    }

    public void JumpToFirstDiff()
    {
        var ok = TimelineViewModel.JumpToFirstDiff();
        if (ok && TimelineViewModel.SelectedDiffItem is not null)
        {
            PureTimelineCurrentFrame = TimelineViewModel.SelectedDiffItem.Frame;
        }

        if (ok)
        {
            TrySetHostFrame(PureTimelineCurrentFrame, "先頭差分へ移動");
        }
    }

    public void JumpToLastDiff()
    {
        var ok = TimelineViewModel.JumpToLastDiff();
        if (ok && TimelineViewModel.SelectedDiffItem is not null)
        {
            PureTimelineCurrentFrame = TimelineViewModel.SelectedDiffItem.Frame;
        }

        if (ok)
        {
            TrySetHostFrame(PureTimelineCurrentFrame, "末尾差分へ移動");
        }
    }

    public void JumpToPreviousDiffFromCurrentFrame()
    {
        var ok = TimelineViewModel.JumpToPreviousDiffFromCurrentFrame();
        if (ok && TimelineViewModel.SelectedDiffItem is not null)
        {
            PureTimelineCurrentFrame = TimelineViewModel.SelectedDiffItem.Frame;
        }

        if (ok)
        {
            TrySetHostFrame(PureTimelineCurrentFrame, "前の差分へ移動");
        }
    }

    public void JumpToNextDiffFromCurrentFrame()
    {
        var ok = TimelineViewModel.JumpToNextDiffFromCurrentFrame();
        if (ok && TimelineViewModel.SelectedDiffItem is not null)
        {
            PureTimelineCurrentFrame = TimelineViewModel.SelectedDiffItem.Frame;
        }

        if (ok)
        {
            TrySetHostFrame(PureTimelineCurrentFrame, "次の差分へ移動");
        }
    }

    public void OpenExperimentalDiagnosticsHost()
    {
        try
        {
            var vm = new ExperimentalYmmTimelineHostViewModel();
            var window = new ExperimentalYmmTimelineHostWindow(vm);
            var owner = System.Windows.Application.Current?.Windows.OfType<System.Windows.Window>().FirstOrDefault(x => x.IsActive);
            if (owner is not null && !ReferenceEquals(owner, window))
            {
                window.Owner = owner;
            }

            window.Show();
        }
        catch (Exception ex)
        {
            logger.Error(ex, "OpenExperimentalDiagnosticsHost failed");
        }
    }

    public async Task LoadSnapshotsDiffAsync(string projectPath, string leftSnapshotId, string rightSnapshotId)
    {
        var leftPath = snapshotService.TryGetNormalizedPath(projectPath, leftSnapshotId);
        var rightPath = snapshotService.TryGetNormalizedPath(projectPath, rightSnapshotId);
        if (string.IsNullOrWhiteSpace(leftPath) || string.IsNullOrWhiteSpace(rightPath))
        {
            return;
        }

        var left = await File.ReadAllTextAsync(leftPath).ConfigureAwait(true);
        var right = await File.ReadAllTextAsync(rightPath).ConfigureAwait(true);
        ApplyDiff(left, right);
        Title = $"差分: {leftSnapshotId} -> {rightSnapshotId}";
    }

    public async Task LoadCurrentVsSnapshotDiffAsync(string projectPath, string snapshotId)
    {
        var snapshotPath = snapshotService.TryGetNormalizedPath(projectPath, snapshotId);
        if (string.IsNullOrWhiteSpace(snapshotPath) || !File.Exists(projectPath))
        {
            return;
        }

        var current = await normalizeService.NormalizeFileAsync(projectPath).ConfigureAwait(true);
        var snapshot = await File.ReadAllTextAsync(snapshotPath).ConfigureAwait(true);
        ApplyDiff(snapshot, current);
        Title = $"差分: {snapshotId} -> 現在";
    }

    private void TryValidateStandalonePipeline()
    {
        try
        {
            if (!standaloneConfig.ShadowValidationEnabled)
            {
                StandaloneValidationStatus = new DiffTimelineStandaloneValidationStatus(
                    Attempted: false,
                    IsSuccess: false,
                    CacheHit: false,
                    SnapshotSource: "disabled",
                    FallbackReason: "shadow-validation-disabled",
                    StageSummary: "disabled",
                    DiagnosticsPath: string.Empty,
                    Errors: [],
                    Warnings: []);
                return;
            }

            var source = "sample-fallback";
            var snapshots = TryBuildSnapshotsFromProjectFiles(null, null);
            if (snapshots is null)
            {
                snapshots = SampleDiffTimelineSnapshotFactory.CreateForSelfCheck();
            }
            else
            {
                source = "normalized-json-adapter";
            }

            var (oldSnapshot, newSnapshot) = snapshots.Value;
            var envelope = DiffTimelineStandalonePipeline.BuildEnvelopeFromSnapshots(
                oldSnapshot,
                newSnapshot,
                new DiffTimelineStandalonePipelineOptions(
                    OptionSnapshot: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["caller"] = nameof(ProjectDiffViewModel),
                        ["entry"] = nameof(TryValidateStandalonePipeline),
                        ["snapshotSource"] = source,
                    },
                    SnapshotCache: standalonePipelineCache));
            if (!envelope.IsSuccess || envelope.Result is null)
            {
                var reason = string.IsNullOrWhiteSpace(envelope.FallbackReason) ? "pipeline-envelope-failed" : envelope.FallbackReason;
                StandaloneValidationStatus = new DiffTimelineStandaloneValidationStatus(
                    Attempted: true,
                    IsSuccess: false,
                    CacheHit: envelope.CacheHit,
                    SnapshotSource: envelope.SnapshotSource,
                    FallbackReason: reason,
                    StageSummary: "pipeline-failed",
                    DiagnosticsPath: string.Empty,
                    Errors: envelope.Errors,
                    Warnings: envelope.Warnings);
                logger.Info($"Standalone pipeline validation failed: {reason}");
                return;
            }

            var selfCheck = DiffTimelineStandalonePipelineSelfCheck.Run();
            var existingSummary = BuildExistingRouteSummary();
            var comparer = DiffTimelineValidationComparer.Compare(existingSummary, envelope.Result);
            var readiness = DiffTimelinePromotionReadinessEvaluator.Evaluate(comparer, envelope);
            var gate = DiffTimelineStandalonePromotionGate.Evaluate(readiness);
            var routeValidationReport = DiffTimelineStandalonePromotionGate.BuildReport(
                requestedRoute: "shadow-validation",
                selectedRoute: gate.Allowed ? "standalone-shadow" : "legacy-shadow",
                readiness: readiness,
                cacheHit: envelope.CacheHit,
                diagnosticsPath: string.Empty,
                rollbackReason: source == "sample-fallback" ? "project-snapshot-unavailable" : "none",
                policy: DiffTimelineStandaloneConfigResolver.BuildPolicy(standaloneConfig));
            var provisionalHistory = new DiffTimelineValidationRunHistory([]);
            var provisionalTrend = DiffTimelineValidationRegressionDetector.EvaluateTrend(provisionalHistory);
            var rollbackGuard = DiffTimelineStandaloneRollbackGuard.Evaluate(routeValidationReport, provisionalHistory, standaloneConfig, provisionalTrend);
            var dashboard = DiffTimelineValidationDashboardBuilder.Build(routeValidationReport, provisionalTrend, rollbackGuard, provisionalHistory);
            var diagnosticsPath = DiffTimelineStandalonePipelineDiagnosticsWriter.WriteToFile(
                directory: Path.Combine(AppContext.BaseDirectory, "diagnostics"),
                result: envelope.Result,
                roundTrip: selfCheck.RoundTrip,
                fallbackReason: source == "sample-fallback" ? "project-snapshot-unavailable" : "none",
                existingRouteSummary: existingSummary,
                comparerResult: comparer,
                promotionReadiness: readiness,
                routeSelection: StandaloneRouteSelectionResult,
                environmentFlags: standaloneConfig.ToEnvironmentSnapshot(),
                routeValidationReport: routeValidationReport,
                validationDashboard: dashboard,
                diagnosticsVerbosity: standaloneConfig.DiagnosticsVerbosity);

            var runRecord = new DiffTimelineValidationRunRecord(
                Timestamp: DateTimeOffset.Now,
                ProjectIdentity: $"{oldSnapshot.ProjectId}->{newSnapshot.ProjectId}",
                OldSnapshotHash: oldSnapshot.Metadata.SnapshotHash,
                NewSnapshotHash: newSnapshot.Metadata.SnapshotHash,
                RequestedRoute: routeValidationReport.RequestedRoute,
                SelectedRoute: routeValidationReport.SelectedRoute,
                GateAllowed: routeValidationReport.GateAllowed,
                GateReason: routeValidationReport.GateReason,
                ComparerConfidence: routeValidationReport.ComparerResult.KeyMatchRate,
                Blockers: routeValidationReport.Blockers,
                Warnings: routeValidationReport.Warnings,
                CacheHit: routeValidationReport.CacheHit,
                DiagnosticsPath: diagnosticsPath,
                FinalRecommendation: routeValidationReport.FinalRecommendation,
                FallbackReason: routeValidationReport.RollbackReason);
            var historyPath = DiffTimelineValidationRunHistoryWriter.Append(
                Path.Combine(AppContext.BaseDirectory, "diagnostics"),
                runRecord,
                standaloneConfig.HistoryKeepCount);
            var history = DiffTimelineValidationRunHistoryWriter.Load(historyPath);
            var trend = DiffTimelineValidationRegressionDetector.EvaluateTrend(history);
            var guardedReport = routeValidationReport with { DiagnosticsPath = diagnosticsPath };
            var guardedRollback = DiffTimelineStandaloneRollbackGuard.Evaluate(guardedReport, history, standaloneConfig, trend);
            var finalDashboard = DiffTimelineValidationDashboardBuilder.Build(guardedReport, trend, guardedRollback, history);
            var docsPath = Path.Combine(AppContext.BaseDirectory, "docs", "difftimeline-standalone-pipeline.md");
            if (!File.Exists(docsPath))
            {
                docsPath = Path.Combine(Directory.GetCurrentDirectory(), "docs", "difftimeline-standalone-pipeline.md");
            }
            var previewRunner = DiffTimelinePreviewValidationRunner.Run(
                diagnosticsDirectory: Path.Combine(AppContext.BaseDirectory, "diagnostics"),
                routeValidationReport: guardedReport,
                history: history,
                dashboard: finalDashboard,
                config: standaloneConfig,
                trend: trend,
                rollbackGuard: guardedRollback,
                docsPath: docsPath,
                commitHash: "99dff2c",
                filteredResult: latestFilteredResult,
                snapshotBrowserState: SnapshotBrowserStateForExport(),
                comparisonHistory: comparisonHistoryStore.Load());
            var previewReadiness = previewRunner.PreviewReadiness;
            var exportPackage = previewRunner.ExportPackage;

            StandaloneValidationStatus = new DiffTimelineStandaloneValidationStatus(
                Attempted: true,
                IsSuccess: true,
                CacheHit: envelope.CacheHit,
                SnapshotSource: envelope.SnapshotSource,
                FallbackReason: source == "sample-fallback" ? "project-snapshot-unavailable" : "none",
                StageSummary: $"{envelope.Result.Diagnostics.StageSummary} | promote={readiness.CanPromote} conf={readiness.Confidence:F2} trend={trend.Recommendation} rollback={guardedRollback.Allowed} export={exportPackage.Succeeded} preview={previewReadiness.CanPreview} package={previewRunner.Succeeded}",
                DiagnosticsPath: diagnosticsPath,
                Errors: envelope.Errors,
                Warnings: envelope.Warnings);
            logger.Info($"Standalone pipeline validation succeeded: {diagnosticsPath}");
        }
        catch (Exception ex)
        {
            StandaloneValidationStatus = new DiffTimelineStandaloneValidationStatus(
                Attempted: true,
                IsSuccess: false,
                CacheHit: false,
                SnapshotSource: "unknown",
                FallbackReason: "exception",
                StageSummary: "exception",
                DiagnosticsPath: string.Empty,
                Errors: [ex.Message],
                Warnings: []);
            logger.Error(ex, "Standalone pipeline validation skipped. fallback remains active.");
        }
    }

    private DiffTimelineExistingRouteSummary BuildExistingRouteSummary()
    {
        var keys = YmmDiffEntries
            .Select(x => $"{x.Kind}|{x.Scope}|{x.Field}|{x.Frame}|{x.Layer}|{x.Length}")
            .ToList();
        var added = YmmDiffEntries.Count(x => string.Equals(x.Kind, "追加", StringComparison.Ordinal));
        var removed = YmmDiffEntries.Count(x => string.Equals(x.Kind, "削除", StringComparison.Ordinal));
        var changed = YmmDiffEntries.Count(x => string.Equals(x.Kind, "変更", StringComparison.Ordinal) || string.Equals(x.Kind, "移動", StringComparison.Ordinal));
        return new DiffTimelineExistingRouteSummary(
            ItemCount: YmmDiffEntries.Count,
            GroupCount: DiffGroups.Count,
            AddedCount: added,
            RemovedCount: removed,
            ChangedCount: changed,
            Keys: keys);
    }

    private (DiffTimelineProjectSnapshot OldSnapshot, DiffTimelineProjectSnapshot NewSnapshot)? TryBuildSnapshotsFromProjectFiles(
        string? oldProjectPath,
        string? newProjectPath)
    {
        var (resolvedOldPath, resolvedNewPath, source) = ResolveStandaloneValidationProjectPaths(oldProjectPath, newProjectPath);
        if (string.IsNullOrWhiteSpace(resolvedOldPath) || string.IsNullOrWhiteSpace(resolvedNewPath))
        {
            logger.Info("Standalone snapshot input: unresolved project paths. source=none");
            return null;
        }

        if (!File.Exists(resolvedOldPath) || !File.Exists(resolvedNewPath))
        {
            logger.Info($"Standalone snapshot input: file not found. source={source} old={resolvedOldPath} new={resolvedNewPath}");
            return null;
        }

        var oldId = $"project-old:{source}";
        var newId = $"project-new:{source}";
        var adapter = new YmmNormalizedJsonSnapshotAdapter(message => logger.Info(message));
        var oldJson = normalizeService.NormalizeFileAsync(resolvedOldPath).GetAwaiter().GetResult();
        var newJson = normalizeService.NormalizeFileAsync(resolvedNewPath).GetAwaiter().GetResult();
        var oldSnapshot = adapter.Convert(oldId, Path.GetFileNameWithoutExtension(resolvedOldPath), resolvedOldPath, oldJson);
        var newSnapshot = adapter.Convert(newId, Path.GetFileNameWithoutExtension(resolvedNewPath), resolvedNewPath, newJson);
        logger.Info($"Standalone snapshot input resolved. source={source} oldHash={oldSnapshot.Metadata.SnapshotHash} newHash={newSnapshot.Metadata.SnapshotHash}");
        return (oldSnapshot, newSnapshot);
    }

    private (string? OldPath, string? NewPath, string Source) ResolveStandaloneValidationProjectPaths(string? oldProjectPath, string? newProjectPath)
    {
        if (!string.IsNullOrWhiteSpace(oldProjectPath) && !string.IsNullOrWhiteSpace(newProjectPath))
        {
            return (oldProjectPath, newProjectPath, "explicit");
        }

        var envOld = Environment.GetEnvironmentVariable("YMM_STANDALONE_VALIDATION_OLD_PATH");
        var envNew = Environment.GetEnvironmentVariable("YMM_STANDALONE_VALIDATION_NEW_PATH");
        if (!string.IsNullOrWhiteSpace(envOld) && !string.IsNullOrWhiteSpace(envNew))
        {
            return (envOld, envNew, "env");
        }

        var fixtureOld = Path.Combine(AppContext.BaseDirectory, "YMMProjectManager.Benchmarks", "Fixtures", "modified-text", "before.ymmp");
        var fixtureNew = Path.Combine(AppContext.BaseDirectory, "YMMProjectManager.Benchmarks", "Fixtures", "modified-text", "after.ymmp");
        if (File.Exists(fixtureOld) && File.Exists(fixtureNew))
        {
            return (fixtureOld, fixtureNew, "fixtures-appbase");
        }

        var cwdFixtureOld = Path.Combine(Directory.GetCurrentDirectory(), "YMMProjectManager.Benchmarks", "Fixtures", "modified-text", "before.ymmp");
        var cwdFixtureNew = Path.Combine(Directory.GetCurrentDirectory(), "YMMProjectManager.Benchmarks", "Fixtures", "modified-text", "after.ymmp");
        if (File.Exists(cwdFixtureOld) && File.Exists(cwdFixtureNew))
        {
            return (cwdFixtureOld, cwdFixtureNew, "fixtures-cwd");
        }

        return (oldProjectPath, newProjectPath, "none");
    }

    private void ApplyDiff(string before, string after)
    {
        try
        {
            JsonDiffEntries.Clear();
            YmmDiffEntries.Clear();
            DiffGroups.Clear();

            foreach (var x in jsonDiffService.Diff(before, after))
            {
                JsonDiffEntries.Add(new DiffEntryViewModel
                {
                    Id = $"json-{JsonDiffEntries.Count}",
                    Kind = DiffTimelineDisplayLabelResolver.ToDiffKindLabel(x.Kind.ToString()),
                    Scope = x.Path,
                    Field = "JSON",
                    Before = DiffDisplayTextService.ToDisplayText(x.Before),
                    After = DiffDisplayTextService.ToDisplayText(x.After),
                });
            }

            var ymmResult = ymmDiffService.DiffWithStatistics(before, after);
            var coreResult = BuildCoreResultFromCurrentRoute(ymmResult);
            latestCoreResult = coreResult;

            var timelineItems = new List<DiffTimelineItemViewModel>(coreResult.RowSet.Rows.Count);
            for (var i = 0; i < coreResult.RowSet.Rows.Count; i++)
            {
                var row = coreResult.RowSet.Rows[i];
                YmmDiffEntries.Add(new DiffEntryViewModel
                {
                    Id = row.RowId,
                    Kind = row.DiffKind,
                    Scope = row.Path,
                    Field = row.Field,
                    Before = row.OldValue,
                    After = row.NewValue,
                    TimelineIndex = row.TimelineIndex,
                    Layer = row.Layer,
                    Frame = row.Frame,
                    Length = row.Length,
                });

                timelineItems.Add(TimelineViewModel.CreateItem(
                    id: row.RowId,
                    kind: row.DiffKind,
                    category: row.SemanticCategory,
                    displayName: row.DisplayLabel,
                    timelineIndex: row.TimelineIndex,
                    layer: row.Layer,
                    frame: row.Frame,
                    length: row.Length,
                    oldValue: row.OldValue,
                    newValue: row.NewValue));
            }

            BuildGroups(coreResult.Groups);
            MatchStatisticsText = FormatStatistics(ymmResult.Statistics, coreResult.Summary);
            TimelineViewModel.SetItems(timelineItems);
            RefreshSnapshotBrowserState("diff-applied");
            ApplyStandaloneFiltersAndGrouping();
            SelectedYmmDiffEntry = YmmDiffEntries.FirstOrDefault();
            SelectedSyncState = TimelineSyncState.Synced;
            TrySetHostFrame(PureTimelineCurrentFrame, "差分読み込み");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "ApplyDiff failed");
            MatchStatisticsText = "統計の計算に失敗しました。";
            SelectedSyncState = TimelineSyncState.Error;
            OnPropertyChanged(nameof(LastSyncAction));
        }
    }

    private void ApplySyncModeAndState()
    {
        TimelineViewModel.SetSyncMode(SelectedTimelineMode, SelectedSyncState);
        OnPropertyChanged(nameof(PureTimelineSyncState));
        OnPropertyChanged(nameof(PureTimelineMode));
    }

    private void BuildGroups(IReadOnlyList<DiffTimelineCoreGroup> coreGroups)
    {
        foreach (var group in coreGroups)
        {
            var items = YmmDiffEntries
                .Where(x => group.ItemIds.Contains(x.Id))
                .ToList();
            DiffGroups.Add(new DiffGroupViewModel
            {
                GroupName = group.GroupDisplayLabel,
                Items = items,
                Count = group.Count,
            });
        }
    }

    public void ClearStandaloneFilters()
    {
        filterSearchText = string.Empty;
        selectedChangeTypes.Clear();
        selectedSemanticCategories.Clear();
        selectedPathFilters.Clear();
        selectedGroupFilters.Clear();
        changedOnlyFilter = false;
        warningOnlyFilter = false;
        selectedChangeTypeFilter = "All";
        selectedSemanticCategoryFilter = "All";
        selectedPathFilter = string.Empty;
        selectedGroupFilter = string.Empty;
        OnPropertyChanged(nameof(FilterSearchText));
        OnPropertyChanged(nameof(ChangedOnlyFilter));
        OnPropertyChanged(nameof(WarningOnlyFilter));
        OnPropertyChanged(nameof(SelectedChangeTypeFilter));
        OnPropertyChanged(nameof(SelectedSemanticCategoryFilter));
        OnPropertyChanged(nameof(SelectedPathFilter));
        OnPropertyChanged(nameof(SelectedGroupFilter));
        ApplyStandaloneFiltersAndGrouping();
    }

    public void SetChangeTypeFilters(IEnumerable<string> values)
    {
        selectedChangeTypes.Clear();
        foreach (var v in values) selectedChangeTypes.Add(v);
        ApplyStandaloneFiltersAndGrouping();
    }

    public void SetSemanticCategoryFilters(IEnumerable<string> values)
    {
        selectedSemanticCategories.Clear();
        foreach (var v in values) selectedSemanticCategories.Add(v);
        ApplyStandaloneFiltersAndGrouping();
    }

    public void SetPathFilters(IEnumerable<string> values)
    {
        selectedPathFilters.Clear();
        foreach (var v in values) selectedPathFilters.Add(v);
        ApplyStandaloneFiltersAndGrouping();
    }

    private void ApplyStandaloneFiltersAndGrouping()
    {
        if (latestCoreResult is null)
        {
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var filterState = new DiffTimelineFilterState(
            PathFilters: selectedPathFilters.ToList(),
            SemanticCategoryFilters: selectedSemanticCategories.ToList(),
            ChangeTypeFilters: selectedChangeTypes.ToList(),
            GroupFilters: selectedGroupFilters.ToList(),
            SearchQuery: new DiffTimelineSearchQuery(filterSearchText, CaseSensitive: false, Regex: false),
            ChangedOnly: changedOnlyFilter,
            WarningOnly: warningOnlyFilter);
        latestFilteredResult = DiffTimelineFilterSearchPipeline.Apply(latestCoreResult, filterState);
        latestGroupStates = ResolveGroupStates(latestCoreResult, selectedGroupingMode);
        sw.Stop();
        lastFilterDuration = sw.Elapsed;
        OnPropertyChanged(nameof(LastFilterDiagnostics));
        OnPropertyChanged(nameof(ActiveFilterSummary));
        OnPropertyChanged(nameof(NoMatchStateText));
    }

    private static IReadOnlyList<DiffTimelineGroupState> ResolveGroupStates(DiffTimelineCoreResult coreResult, string mode)
    {
        return mode switch
        {
            "Semantic" => DiffTimelineGroupingUxResolver.BuildGroupStates(coreResult, "semantic"),
            "Timeline" => DiffTimelineGroupingUxResolver.BuildGroupStates(coreResult, "timeline"),
            "Layer" => DiffTimelineGroupingUxResolver.BuildGroupStates(coreResult, "layer"),
            "Field" => DiffTimelineGroupingUxResolver.BuildGroupStates(coreResult, "field"),
            "Path" => DiffTimelineGroupingUxResolver.BuildGroupStates(coreResult, "path"),
            "ChangeType" => DiffTimelineGroupingUxResolver.BuildGroupStates(coreResult, "changeType"),
            _ => [],
        };
    }

    public void CollapseAllGroups() => latestGroupStates = latestGroupStates.Select(x => x with { Collapsed = true }).ToList();
    public void ExpandAllGroups() => latestGroupStates = latestGroupStates.Select(x => x with { Collapsed = false }).ToList();
    public void SelectSnapshotAsOld()
    {
        SnapshotBrowser.SelectOldSnapshot(SelectedSnapshotListItem);
        TrackManualUiAction("SnapshotSelected", "set-old");
        OnPropertyChanged(nameof(SnapshotCompareSummaryText));
    }

    public void SelectSnapshotAsNew()
    {
        SnapshotBrowser.SelectNewSnapshot(SelectedSnapshotListItem);
        TrackManualUiAction("SnapshotSelected", "set-new");
        OnPropertyChanged(nameof(SnapshotCompareSummaryText));
    }

    public void SwapSnapshotSelection()
    {
        SnapshotBrowser.SwapSelection();
        TrackManualUiAction("SnapshotSwapped", "swap");
        OnPropertyChanged(nameof(SnapshotCompareSummaryText));
    }

    public void ClearSnapshotSelection()
    {
        SnapshotBrowser.ClearSelection();
        TrackManualUiAction("SnapshotCleared", "clear");
        OnPropertyChanged(nameof(SnapshotCompareSummaryText));
    }

    public string SnapshotCompareSummaryText => SnapshotBrowser.CompareSummaryText;
    public void RunSelectedSnapshotCompare()
    {
        if (SnapshotBrowser.IsCompareRunning)
        {
            SnapshotBrowser.LastCompareStatusText = "blocked";
            SnapshotBrowser.LastCompareErrorText = "Compare is already running.";
            TrackManualUiAction("CompareBlocked", "already-running");
            PersistManualValidationLog();
            return;
        }

        SnapshotBrowser.IsCompareRunning = true;
        SnapshotBrowser.LastCompareErrorText = string.Empty;
        SnapshotBrowser.LastCompareStatusText = "running (preview/manual)";
        SnapshotBrowser.LastCompareResultSummary = string.Empty;
        TrackManualUiAction("CompareStarted", "started");
        try
        {
            var request = SnapshotBrowser.BuildCompareRequest();
            if (request is null)
            {
                SnapshotBrowser.LastCompareStatusText = "blocked";
                SnapshotBrowser.LastCompareErrorText = "Select valid old/new snapshots before compare.";
                TrackManualUiAction("CompareBlocked", "invalid-selection");
                PersistManualValidationLog();
                return;
            }

            if (!snapshotRepository.TryGetSnapshotByHash(request.OldSnapshotHash, out var oldSnapshot) ||
                !snapshotRepository.TryGetSnapshotByHash(request.NewSnapshotHash, out var newSnapshot) ||
                oldSnapshot is null || newSnapshot is null)
            {
                SnapshotBrowser.LastCompareStatusText = "no-op";
                SnapshotBrowser.LastCompareErrorText = "Snapshot body is missing in repository. Compare skipped.";
                TrackManualUiAction("CompareNoOp", "snapshot-body-missing");
                PersistManualValidationLog();
                return;
            }

            var envelope = DiffTimelineStandalonePipeline.BuildEnvelopeFromSnapshots(
                oldSnapshot,
                newSnapshot,
                new DiffTimelineStandalonePipelineOptions(
                    OptionSnapshot: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["requestedRoute"] = "snapshot-browser-preview",
                        ["compareMode"] = "manual-preview",
                    },
                    SnapshotCache: standalonePipelineCache));
            if (!envelope.IsSuccess || envelope.Result is null)
            {
                SnapshotBrowser.LastCompareStatusText = "failed";
                SnapshotBrowser.LastCompareErrorText = string.IsNullOrWhiteSpace(envelope.FallbackReason) ? "pipeline failed" : envelope.FallbackReason;
                TrackManualUiAction("CompareFailed", SnapshotBrowser.LastCompareErrorText);
                PersistManualValidationLog();
                return;
            }

            latestCoreResult = envelope.Result.CoreResult;
            comparisonHistoryStore.Append(new DiffTimelineComparisonHistoryEntry(
                OldSnapshotHash: request.OldSnapshotHash,
                NewSnapshotHash: request.NewSnapshotHash,
                ComparedAt: DateTimeOffset.Now,
                Summary: $"manual-preview: rows={envelope.Result.CoreResult.RowSet.Rows.Count}",
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["cacheHit"] = envelope.CacheHit.ToString(),
                    ["source"] = envelope.SnapshotSource,
                    ["groupCount"] = envelope.Result.CoreResult.Groups.Count.ToString(),
                }));

            YmmDiffEntries.Clear();
            foreach (var row in envelope.Result.CoreResult.RowSet.Rows)
            {
                YmmDiffEntries.Add(new DiffEntryViewModel
                {
                    Id = row.RowId,
                    Kind = row.DiffKind,
                    Scope = row.Path,
                    Field = row.Field,
                    Before = row.OldValue,
                    After = row.NewValue,
                    TimelineIndex = row.TimelineIndex,
                    Layer = row.Layer,
                    Frame = row.Frame,
                    Length = row.Length,
                });
            }
            DiffGroups.Clear();
            BuildGroups(envelope.Result.CoreResult.Groups);
            MatchStatisticsText = envelope.Result.CoreResult.Summary.SummaryText;
            RefreshSnapshotBrowserState("snapshot-compare");
            ApplyStandaloneFiltersAndGrouping();

            var d = envelope.Result.Diagnostics;
            SnapshotBrowser.LastCompareResultSummary = $"added={d.AddedCount}, removed={d.RemovedCount}, changed={d.ChangedCount}, rows={d.RowCount}, groups={d.GroupCount}, cacheHit={envelope.CacheHit}";
            SnapshotBrowser.LastCompareDiagnosticsPath = Path.Combine(AppContext.BaseDirectory, "diagnostics");
            SnapshotBrowser.LastCompareTimestamp = DateTimeOffset.Now;
            SnapshotBrowser.LastCompareStatusText = "success (preview/manual)";
            TrackManualUiAction("CompareSucceeded", SnapshotBrowser.LastCompareResultSummary);
            PersistManualValidationLog();
        }
        catch (Exception ex)
        {
            SnapshotBrowser.LastCompareStatusText = "failed";
            SnapshotBrowser.LastCompareErrorText = ex.Message;
            TrackManualUiAction("CompareFailed", ex.Message);
            PersistManualValidationLog();
            logger.Error(ex, "RunSelectedSnapshotCompare failed");
        }
        finally
        {
            SnapshotBrowser.IsCompareRunning = false;
        }
    }
    private void TrackManualUiAction(string actionType, string stateSummary)
    {
        manualUiActions.Add(new DiffTimelineManualUiAction(
            actionType,
            DateTimeOffset.Now,
            stateSummary,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["searchText"] = FilterSearchText ?? string.Empty,
                ["groupingMode"] = SelectedGroupingMode,
                ["compareSummary"] = SnapshotBrowser.CompareSummaryText,
            }));
    }

    private void PersistManualValidationLog()
    {
        var selected = SnapshotBrowser.BuildCompareRequest();
        var diagnosticsDir = Path.Combine(AppContext.BaseDirectory, "diagnostics");
        var log = new DiffTimelineManualUiValidationLog(
            SessionId: manualValidationSessionId,
            CreatedAt: DateTimeOffset.Now,
            Actions: manualUiActions.ToList(),
            SelectedOldSnapshotHash: selected?.OldSnapshotHash ?? "(none)",
            SelectedNewSnapshotHash: selected?.NewSnapshotHash ?? "(none)",
            CompareRequestSummary: SnapshotBrowser.CompareSummaryText,
            CompareSucceeded: string.Equals(SnapshotBrowser.LastCompareStatusText, "success (preview/manual)", StringComparison.Ordinal),
            BlockedOrNoOpReason: SnapshotBrowser.LastCompareStatusText is "blocked" or "no-op" ? SnapshotBrowser.LastCompareErrorText : string.Empty,
            DiagnosticsPath: SnapshotBrowser.LastCompareDiagnosticsPath,
            ExportPackagePath: string.Empty,
            LatestStatusText: SnapshotBrowser.LastCompareStatusText,
            LatestErrorText: SnapshotBrowser.LastCompareErrorText);
        LatestManualValidationLogPath = DiffTimelineManualUiValidationLogWriter.Write(diagnosticsDir, log);
        var summary = new DiffTimelineManualUiValidationSessionSummary(
            SessionId: manualValidationSessionId,
            UpdatedAt: DateTimeOffset.Now,
            CompareCount: manualUiActions.Count(x => x.ActionType.StartsWith("Compare", StringComparison.Ordinal)),
            BlockedCount: manualUiActions.Count(x => x.ActionType == "CompareBlocked"),
            NoOpCount: manualUiActions.Count(x => x.ActionType == "CompareNoOp"),
            FailureCount: manualUiActions.Count(x => x.ActionType == "CompareFailed"),
            LatestDiagnosticsPath: SnapshotBrowser.LastCompareDiagnosticsPath,
            LatestExportPath: string.Empty,
            LatestResult: SnapshotBrowser.LastCompareStatusText);
        DiffTimelineManualUiValidationLogWriter.WriteSummary(diagnosticsDir, summary);
        LatestManualValidationSummary = $"compare={summary.CompareCount}, blocked={summary.BlockedCount}, noop={summary.NoOpCount}, failed={summary.FailureCount}";
    }
    private DiffTimelineSnapshotBrowserState SnapshotBrowserStateForExport()
    {
        var selected = SnapshotBrowser.BuildCompareRequest();
        var latest = SnapshotBrowser.LatestValidationState;
        var selectedDetail = new DiffTimelineSnapshotDetailSummary(
            SnapshotHash: selected?.NewSnapshotHash ?? "(none)",
            TimelineCount: SnapshotBrowser.SnapshotList.Count,
            LayerCount: SnapshotBrowser.ComparisonCandidates.Count,
            ItemCount: selected is null ? 0 : 1,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["selectedOldSnapshotHash"] = selected?.OldSnapshotHash ?? "(none)",
                ["selectedNewSnapshotHash"] = selected?.NewSnapshotHash ?? "(none)",
                ["canCompare"] = (selected is not null).ToString(),
                ["compareSummary"] = SnapshotBrowser.CompareSummaryText,
                ["snapshotCount"] = SnapshotBrowser.SnapshotList.Count.ToString(),
                ["comparisonCandidateCount"] = SnapshotBrowser.ComparisonCandidates.Count.ToString(),
            });
        return new DiffTimelineSnapshotBrowserState(
            Snapshots: SnapshotBrowser.SnapshotList.ToList(),
            ComparisonCandidates: SnapshotBrowser.ComparisonCandidates.ToList(),
            SelectedSnapshotDetail: selectedDetail,
            LatestValidationState: string.IsNullOrWhiteSpace(latest)
                ? "unknown"
                : $"{latest}|selected={(selected is null ? "none" : $"{selected.OldSnapshotHash}->{selected.NewSnapshotHash}")}");
    }

    private void RefreshSnapshotBrowserState(string validationState)
    {
        if (latestCoreResult is null)
        {
            return;
        }

        var oldHash = latestCoreResult.Summary.BuildOptionsSnapshot.GetValueOrDefault("oldSnapshotHash") ?? $"legacy-{DateTimeOffset.Now:yyyyMMddHHmmss}";
        var newHash = latestCoreResult.Summary.BuildOptionsSnapshot.GetValueOrDefault("newSnapshotHash") ?? $"legacy-new-{DateTimeOffset.Now:yyyyMMddHHmmss}";
        var oldSnapshot = SampleDiffTimelineSnapshotFactory.CreateForSelfCheck().OldSnapshot with
        {
            Metadata = SampleDiffTimelineSnapshotFactory.CreateForSelfCheck().OldSnapshot.Metadata with { SnapshotHash = oldHash }
        };
        var newSnapshot = SampleDiffTimelineSnapshotFactory.CreateForSelfCheck().NewSnapshot with
        {
            Metadata = SampleDiffTimelineSnapshotFactory.CreateForSelfCheck().NewSnapshot.Metadata with { SnapshotHash = newHash }
        };
        snapshotRepository.SaveSnapshot(new DiffTimelineSnapshotRepositoryEntry(oldSnapshot, "project-old", "ProjectDiffViewModel", "auto-captured", ["preview"], DateTimeOffset.Now.AddSeconds(-1)));
        snapshotRepository.SaveSnapshot(new DiffTimelineSnapshotRepositoryEntry(newSnapshot, "project-new", "ProjectDiffViewModel", "auto-captured", ["preview"], DateTimeOffset.Now));
        comparisonHistoryStore.Append(new DiffTimelineComparisonHistoryEntry(
            OldSnapshotHash: oldHash,
            NewSnapshotHash: newHash,
            ComparedAt: DateTimeOffset.Now,
            Summary: validationState,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rowCount"] = latestCoreResult.RowSet.Rows.Count.ToString(),
                ["groupCount"] = latestCoreResult.Groups.Count.ToString(),
            }));
        var browser = snapshotRepository.BuildBrowserState(validationState);
        SnapshotBrowser.ApplyState(browser);
    }

    private DiffTimelineCoreResult BuildCoreResultFromCurrentRoute(YmmProjectDiffResult ymmResult)
    {
        var requestedRoute = standaloneConfig.StandaloneRouteEnabled ? "standalone" : "legacy-core-builder";
        if (!standaloneConfig.StandaloneRouteEnabled)
        {
            StandaloneRouteSelectionResult = new DiffTimelineRouteSelectionResult(
                RequestedRoute: requestedRoute,
                SelectedRoute: "legacy-core-builder",
                FallbackRoute: "legacy-core-builder",
                Reason: "standalone-route-disabled",
                PromotionReadiness: null,
                DiagnosticsPath: string.Empty);
            return BuildLegacyCoreResult(ymmResult);
        }

        try
        {
            var snapshots = TryBuildSnapshotsFromProjectFiles(null, null);
            if (snapshots is null)
            {
                snapshots = SampleDiffTimelineSnapshotFactory.CreateForSelfCheck();
            }

            var envelope = DiffTimelineStandalonePipeline.BuildEnvelopeFromSnapshots(
                snapshots.Value.OldSnapshot,
                snapshots.Value.NewSnapshot,
                new DiffTimelineStandalonePipelineOptions(
                    OptionSnapshot: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["requestedRoute"] = requestedRoute,
                        ["envRouteFlag"] = standaloneConfig.StandaloneRouteEnabled ? "1" : "0",
                    },
                    SnapshotCache: standalonePipelineCache));

            if (!envelope.IsSuccess || envelope.Result is null)
            {
                StandaloneRouteSelectionResult = new DiffTimelineRouteSelectionResult(
                    RequestedRoute: requestedRoute,
                    SelectedRoute: "legacy-core-builder",
                    FallbackRoute: "legacy-core-builder",
                    Reason: envelope.FallbackReason,
                    PromotionReadiness: null,
                    DiagnosticsPath: string.Empty);
                return BuildLegacyCoreResult(ymmResult);
            }

            var existingSummary = BuildExistingRouteSummary();
            var comparer = DiffTimelineValidationComparer.Compare(existingSummary, envelope.Result);
            var readiness = DiffTimelinePromotionReadinessEvaluator.Evaluate(comparer, envelope);
            var policy = DiffTimelineStandaloneConfigResolver.BuildPolicy(standaloneConfig);
            var gate = DiffTimelineStandalonePromotionGate.Evaluate(readiness, policy);
            var report = DiffTimelineStandalonePromotionGate.BuildReport(
                requestedRoute,
                gate.Allowed ? "standalone" : "legacy-core-builder",
                readiness,
                envelope.CacheHit,
                string.Empty,
                "none",
                policy);
            var historyPath = Path.Combine(AppContext.BaseDirectory, "diagnostics", "difftimeline-validation-run-history.json");
            var history = DiffTimelineValidationRunHistoryWriter.Load(historyPath);
            var trend = DiffTimelineValidationRegressionDetector.EvaluateTrend(history);
            var rollbackGuard = DiffTimelineStandaloneRollbackGuard.Evaluate(report, history, standaloneConfig, trend);
            if (!gate.Allowed)
            {
                StandaloneRouteSelectionResult = new DiffTimelineRouteSelectionResult(
                    RequestedRoute: requestedRoute,
                    SelectedRoute: "legacy-core-builder",
                    FallbackRoute: "legacy-core-builder",
                    Reason: gate.Reason,
                    PromotionReadiness: readiness,
                    DiagnosticsPath: string.Empty);
                return BuildLegacyCoreResult(ymmResult);
            }
            if (!rollbackGuard.Allowed)
            {
                StandaloneRouteSelectionResult = new DiffTimelineRouteSelectionResult(
                    RequestedRoute: requestedRoute,
                    SelectedRoute: "legacy-core-builder",
                    FallbackRoute: "legacy-core-builder",
                    Reason: rollbackGuard.Reason,
                    PromotionReadiness: readiness,
                    DiagnosticsPath: string.Empty);
                return BuildLegacyCoreResult(ymmResult);
            }

            StandaloneRouteSelectionResult = new DiffTimelineRouteSelectionResult(
                RequestedRoute: requestedRoute,
                SelectedRoute: "standalone",
                FallbackRoute: "legacy-core-builder",
                Reason: gate.Reason,
                PromotionReadiness: readiness,
                DiagnosticsPath: string.Empty);
            return envelope.Result.CoreResult;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Standalone route failed; fallback to legacy route.");
            StandaloneRouteSelectionResult = new DiffTimelineRouteSelectionResult(
                RequestedRoute: requestedRoute,
                SelectedRoute: "legacy-core-builder",
                FallbackRoute: "legacy-core-builder",
                Reason: "exception",
                PromotionReadiness: null,
                DiagnosticsPath: string.Empty);
            return BuildLegacyCoreResult(ymmResult);
        }
    }

    private static DiffTimelineCoreResult BuildLegacyCoreResult(YmmProjectDiffResult ymmResult)
    {
        return DiffTimelineCoreBuilder.BuildResult(
            ymmResult.Entries,
            new DiffTimelineCoreBuildOptions(
                KindLabelResolver: x => DiffTimelineDisplayLabelResolver.ToDiffKindLabel(x),
                FieldLabelResolver: x => DiffTimelineDisplayLabelResolver.ToFieldLabel(x),
                ValueDisplayResolver: x => DiffDisplayTextService.ToDisplayText(x?.ToString()),
                OptionSnapshot: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source"] = "ProjectDiffViewModel.ApplyDiff",
                    ["mode"] = "StandaloneCoreV1",
                }));
    }

    private void OnTimelineSelectedDiffItemChanged(DiffTimelineItemViewModel? item)
    {
        if (item is null || isSyncingSelection)
        {
            return;
        }

        var match = YmmDiffEntries.FirstOrDefault(x => string.Equals(x.Id, item.Id, StringComparison.Ordinal));
        if (match is null)
        {
            return;
        }

        isSyncingSelection = true;
        try
        {
            SelectedYmmDiffEntry = match;
            PureTimelineCurrentFrame = match.Frame;
            PureTimelineSelection = match.Id;
        }
        finally
        {
            isSyncingSelection = false;
        }
    }

    private static string FormatStatistics(YmmDiffMatchStatistics s, DiffTimelineCoreSummary summary)
    {
        return string.Join(" | ",
            $"旧={s.OldItemCount}",
            $"新={s.NewItemCount}",
            $"ID一致={s.MatchedByInternalId}",
            $"Fallback一致={s.MatchedByFallback}",
            $"旧未一致={s.UnmatchedOldItems}",
            $"新未一致={s.UnmatchedNewItems}",
            $"追加={s.AddedCount}",
            $"削除={s.RemovedCount}",
            $"移動={s.MovedCount}",
            $"変更={s.ModifiedCount}",
            $"Core={summary.SummaryText}");
    }

    private static string ToSyncStateLabel(TimelineSyncState state)
    {
        return state switch
        {
            TimelineSyncState.Unavailable => "利用不可",
            TimelineSyncState.Detached => "切断",
            TimelineSyncState.Synced => "同期中",
            TimelineSyncState.Manual => "手動",
            TimelineSyncState.Error => "エラー",
            _ => state.ToString(),
        };
    }

    private static string ToTimelineModeLabel(TimelineMode mode)
    {
        return mode switch
        {
            TimelineMode.Standalone => "単独",
            TimelineMode.Synced => "同期",
            TimelineMode.Comparison => "比較",
            _ => mode.ToString(),
        };
    }

    private static string ToAdapterKindLabel(PureTimelineAdapterKind kind)
    {
        return kind switch
        {
            PureTimelineAdapterKind.Placeholder => "プレースホルダー",
            PureTimelineAdapterKind.FutureYmmTimeline => "将来YMMタイムライン",
            _ => kind.ToString(),
        };
    }

    private static string ToPureTimelineStatusLabel(YMMProjectManager.Presentation.Timeline.PureTimelineStatus status)
    {
        return status switch
        {
            YMMProjectManager.Presentation.Timeline.PureTimelineStatus.Unavailable => "利用不可",
            YMMProjectManager.Presentation.Timeline.PureTimelineStatus.Placeholder => "プレースホルダー",
            YMMProjectManager.Presentation.Timeline.PureTimelineStatus.Initializing => "初期化中",
            YMMProjectManager.Presentation.Timeline.PureTimelineStatus.ExperimentalReady => "実験準備完了",
            YMMProjectManager.Presentation.Timeline.PureTimelineStatus.Ready => "準備完了",
            YMMProjectManager.Presentation.Timeline.PureTimelineStatus.Detached => "切断",
            YMMProjectManager.Presentation.Timeline.PureTimelineStatus.Error => "エラー",
            _ => status.ToString(),
        };
    }

    private void TryInitializeHost()
    {
        try
        {
            PureTimelineHost.InitializeAsync().GetAwaiter().GetResult();
            OnPropertyChanged(nameof(PureTimelineStatus));
            OnPropertyChanged(nameof(LastSyncAction));
        }
        catch (Exception ex)
        {
            logger.Error(ex, "PureTimelineHost.Initialize failed");
            SelectedSyncState = TimelineSyncState.Detached;
        }
    }

    private void TrySetHostFrame(int frame, string reason)
    {
        try
        {
            PureTimelineHost.SetCurrentFrameAsync(frame).GetAwaiter().GetResult();
            OnPropertyChanged(nameof(PureTimelineStatus));
            OnPropertyChanged(nameof(LastSyncAction));
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"PureTimelineHost.SetCurrentFrame failed: {reason}");
            SelectedSyncState = TimelineSyncState.Detached;
        }
    }

    private void TryCenterHostFrame(int frame)
    {
        try
        {
            PureTimelineHost.CenterFrameAsync(frame).GetAwaiter().GetResult();
            OnPropertyChanged(nameof(PureTimelineStatus));
            OnPropertyChanged(nameof(LastSyncAction));
        }
        catch (Exception ex)
        {
            logger.Error(ex, "PureTimelineHost.CenterFrame failed");
            SelectedSyncState = TimelineSyncState.Detached;
        }
    }

    public void Dispose()
    {
        PureTimelineHost.Dispose();
    }
}

