
using YMMProjectManager.Presentation.TimelinePresentation.State;
using YMMProjectManager.Presentation.Timeline.Experimental;
using YMMProjectManager.Presentation.Timeline.Experimental.ViewModels;
using YMMProjectManager.Presentation.Timeline.Experimental.Views;
using System.Text.Json;
using System.Windows.Media;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectDiffViewModel : ViewModelBase, IDisposable
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
    private TimeSpan lastGroupingDuration = TimeSpan.Zero;
    private TimeSpan lastCompareApplyDuration = TimeSpan.Zero;
    private TimeSpan lastUiUpdateDuration = TimeSpan.Zero;
    private TimeSpan lastRenderDuration = TimeSpan.Zero;
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
    private string latestReusableSessionSummary = string.Empty;
    private string latestReusableSessionPath = string.Empty;
    private readonly DiffTimelineSnapshotRepository snapshotRepository;
    private readonly DiffTimelineComparisonHistoryStore comparisonHistoryStore;
    private readonly DiffTimelineReusableCompareSessionStore reusableSessionStore;
    private readonly DiffTimelineProjectionCache rowProjectionCache = new();
    private DiffTimelineProjectionCacheStats? latestProjectionCacheStats;
    private IReadOnlyList<DiffTimelineLightweightRowProjection> latestLightweightRows = [];
    private bool isLargeResultMode;
    private string largeResultModeReason = string.Empty;
    private int materializedRowLimit;
    private int totalAvailableRowCount;
    private int displayedRowCount;
    private int deferredRowCount;
    private int visibleRowWindowStart;
    private int visibleRowWindowSize = 500;
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
    public string LatestReusableSessionSummary
    {
        get => latestReusableSessionSummary;
        private set => SetProperty(ref latestReusableSessionSummary, value);
    }
    public string LatestReusableSessionPath
    {
        get => latestReusableSessionPath;
        private set => SetProperty(ref latestReusableSessionPath, value);
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
        : $"matched={latestFilteredResult.MatchedRowCount}, filteredOut={latestFilteredResult.FilteredOutCount}, filterMs={lastFilterDuration.TotalMilliseconds:F1}, groupMs={lastGroupingDuration.TotalMilliseconds:F1}, applyMs={lastCompareApplyDuration.TotalMilliseconds:F1}";
    public bool HasVirtualizationWarning => BuildHeavyProjectDiagnostics().HeavyProjectDetected || BuildHeavyProjectDiagnostics().VirtualizationRecommended;
    public string VirtualizationRecommendationText => BuildHeavyProjectDiagnostics().VirtualizationRecommended
        ? "大きな比較結果です。表示が重くなる可能性があります。仮想化表示の利用を推奨します。"
        : "仮想化表示の推奨は現在不要です。";
    public string CompactRenderDiagnosticsText
    {
        get
        {
            var metricsSnapshot = BuildMetricsSnapshot();
            var state = metricsSnapshot.VirtualizationState;
            var renderMetrics = metricsSnapshot.RenderMetrics;
            return $"行 {state.RowCount:N0} / グループ {state.GroupCount:N0} / 描画 {renderMetrics.LastRenderDuration.TotalMilliseconds:F0}ms / フィルター {renderMetrics.LastFilterDuration.TotalMilliseconds:F0}ms";
        }
    }
    public string DiagnosticsDetailsText
    {
        get
        {
            var metricsSnapshot = BuildMetricsSnapshot();
            var state = metricsSnapshot.VirtualizationState;
            var heavy = metricsSnapshot.HeavyProjectDiagnostics;
            var renderMetrics = metricsSnapshot.RenderMetrics;
            var reasonText = heavy.Reasons.Count == 0 ? "(none)" : string.Join(", ", heavy.Reasons);
            var consistencyWarnings = BuildMetricsSnapshotConsistencyWarnings(metricsSnapshot);
            var warningText = consistencyWarnings.Count == 0 ? "none" : string.Join(", ", consistencyWarnings);
            return $"Render={renderMetrics.LastRenderDuration.TotalMilliseconds:F1}ms, Filter={renderMetrics.LastFilterDuration.TotalMilliseconds:F1}ms, Grouping={renderMetrics.LastGroupingDuration.TotalMilliseconds:F1}ms, CompareApply={renderMetrics.LastCompareApplyDuration.TotalMilliseconds:F1}ms, UIUpdate={renderMetrics.LastUiUpdateDuration.TotalMilliseconds:F1}ms\n" +
                   $"VisibleRows~{state.VisibleRowEstimate:N0}, EstimatedVisuals~{state.EstimatedVisualCount:N0}, EstimatedMemory~{state.EstimatedMemoryUsageBytes / 1024.0 / 1024.0:F2}MB\n" +
                   $"HeavyProjectDetected={heavy.HeavyProjectDetected}, VirtualizationRecommended={heavy.VirtualizationRecommended}, Reasons={reasonText}\n" +
                   $"ProjectionCache={metricsSnapshot.ProjectionCacheStats?.CachedProjectionCount ?? 0}, Materialized={metricsSnapshot.ProjectionCacheStats?.MaterializedRowCount ?? 0}, Reuse={metricsSnapshot.ProjectionCacheStats?.ProjectionReuseCount ?? 0}, Deferred={metricsSnapshot.ProjectionCacheStats?.DeferredProjectionCount ?? 0}\n" +
                   $"LargeResultMode={metricsSnapshot.IsLargeResultMode}, Reason={metricsSnapshot.LargeResultModeReason}, Window={metricsSnapshot.VisibleRowWindowStart}-{metricsSnapshot.VisibleRowWindowStart + metricsSnapshot.DisplayedRowCount}/{metricsSnapshot.TotalAvailableRowCount}\n" +
                   $"MetricsSnapshotConsistencyWarnings={warningText}";
        }
    }
    public bool IsLargeResultMode => isLargeResultMode;
    public string LargeResultModeReason => largeResultModeReason;
    public int MaterializedRowLimit => materializedRowLimit;
    public int TotalAvailableRowCount => totalAvailableRowCount;
    public int DisplayedRowCount => displayedRowCount;
    public int DeferredRowCount => deferredRowCount;
    public int VisibleRowWindowStart => visibleRowWindowStart;
    public int VisibleRowWindowSize => visibleRowWindowSize;
    public bool CanLoadMoreRows => DeferredRowCount > 0;
    public string RowWindowSummaryText => $"表示中: {DisplayedRowCount:N0} / {TotalAvailableRowCount:N0} 行 (遅延: {DeferredRowCount:N0})";
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
        reusableSessionStore = new DiffTimelineReusableCompareSessionStore(diagnosticsRoot);
        PureTimelineHost = new PureTimelineHostViewModel(PureTimelineAdapterKind.Placeholder, pureTimelineExperimentalOptions);

        TimelineViewModel.SelectedDiffItemChanged += OnTimelineSelectedDiffItemChanged;
        ApplySyncModeAndState();
        TryInitializeHost();
        TryValidateStandalonePipeline();
        RefreshReusableSessionState();
        TryWriteRouteARenderPerfDiagnostics(
            new RouteARenderPerfDiagnostics(
                Timestamp: DateTimeOffset.Now,
                MeasurementSource: "ProjectDiffViewModel.ctor",
                ProcessStartTime: SafeGetStartTime(Process.GetCurrentProcess()),
                ProcessUptimeMs: GetProcessUptimeMs(),
                TotalOpenMs: 0,
                SnapshotResolveMs: 0,
                PipelineBuildMs: 0,
                ViewModelCreateMs: 0,
                MaterializationMs: 0,
                VisibleItemsUpdateMs: 0,
                TotalItemCount: 0,
                ProjectedItemCount: 0,
                VisibleItemCount: 0,
                InitialRenderItemCap: TimelineViewModel.InitialRenderItemCap,
                InitialRenderCapApplied: false,
                ProjectionReused: false,
                ProjectionRebuilt: false,
                LastInvalidationReason: "None",
                ProjectionStatusText: "startup-baseline",
                ProcessMetrics: CaptureRelatedProcessMetrics(),
                GpuEnvironmentMetrics: CaptureGpuEnvironmentMetrics()));
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

    public void OpenSceneAwareHistoryPreviewInvestigation()
    {
        try
        {
            var diagnosticsDir = Path.Combine(AppContext.BaseDirectory, "diagnostics");
            var probeResult = SceneAwareHistoryPreviewProbe.Run(diagnosticsDir);
            var vm = new SceneAwareHistoryPreviewInvestigationViewModel(diagnosticsDir);
            vm.Apply(probeResult);
            vm.SetOpenHandler(OpenRouteADetailViewerReadOnlySandbox);
            var window = new SceneAwareHistoryPreviewInvestigationWindow(vm);
            var owner = System.Windows.Application.Current?.Windows.OfType<System.Windows.Window>().FirstOrDefault(x => x.IsActive);
            if (owner is not null && !ReferenceEquals(owner, window))
            {
                window.Owner = owner;
            }

            window.Show();
        }
        catch (Exception ex)
        {
            logger.Error(ex, "OpenSceneAwareHistoryPreviewInvestigation failed");
        }
    }

    private RouteADetailPreviewOpenResult OpenRouteADetailViewerReadOnlySandbox(RouteADetailPreviewOpenRequest request)
    {
        try
        {
            var openSw = System.Diagnostics.Stopwatch.StartNew();
            var snapshotResolveMs = 0L;
            var pipelineBuildMs = 0L;
            var viewModelCreateMs = 0L;
            var materializationMs = 0L;
            var visibleItemsUpdateMs = 0L;
            if (!request.ManualButtonClick || !request.ReadOnly || request.AllowDiffApply || request.AllowHistoryRestore || request.AllowRuntimeMutation || request.OpenMode != "ReadOnlySandbox")
            {
                return new RouteADetailPreviewOpenResult(true, false, true, "Safety guard blocked request.", false, "ReadOnlyDryRun", request.SelectedCandidateId, request.OldSnapshotHash, request.NewSnapshotHash);
            }

            if (string.IsNullOrWhiteSpace(request.OldSnapshotHash) || string.IsNullOrWhiteSpace(request.NewSnapshotHash))
            {
                return new RouteADetailPreviewOpenResult(true, false, true, "Snapshot pair is missing.", false, "ReadOnlyDryRun", request.SelectedCandidateId, request.OldSnapshotHash, request.NewSnapshotHash);
            }

            if (!snapshotRepository.TryGetSnapshotByHash(request.OldSnapshotHash, out var oldSnapshot) ||
                !snapshotRepository.TryGetSnapshotByHash(request.NewSnapshotHash, out var newSnapshot) ||
                oldSnapshot is null || newSnapshot is null)
            {
                return new RouteADetailPreviewOpenResult(true, false, true, "Snapshot body was not found in repository.", false, "ReadOnlyDryRun", request.SelectedCandidateId, request.OldSnapshotHash, request.NewSnapshotHash);
            }
            snapshotResolveMs = openSw.ElapsedMilliseconds;

            var pipelineSw = System.Diagnostics.Stopwatch.StartNew();
            var envelope = DiffTimelineStandalonePipeline.BuildEnvelopeFromSnapshots(
                oldSnapshot,
                newSnapshot,
                new DiffTimelineStandalonePipelineOptions(
                    OptionSnapshot: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["requestedRoute"] = "routeb-scene-aware-readonly-sandbox",
                        ["compareMode"] = "manual-readonly-sandbox",
                    },
                    SnapshotCache: standalonePipelineCache));
            pipelineSw.Stop();
            pipelineBuildMs = pipelineSw.ElapsedMilliseconds;
            if (!envelope.IsSuccess || envelope.Result is null)
            {
                return new RouteADetailPreviewOpenResult(true, false, true, "Standalone pipeline failed.", false, "ReadOnlyDryRun", request.SelectedCandidateId, request.OldSnapshotHash, request.NewSnapshotHash);
            }

            var vmSw = System.Diagnostics.Stopwatch.StartNew();
            var vm = new ProjectDiffViewModel(logger, snapshotService, normalizeService, jsonDiffService, ymmDiffService);
            vmSw.Stop();
            viewModelCreateMs = vmSw.ElapsedMilliseconds;
            vm.latestCoreResult = envelope.Result.CoreResult;
            var matSw = System.Diagnostics.Stopwatch.StartNew();
            vm.MaterializeRowsForCurrentWindow(envelope.Result.CoreResult);
            vm.DiffGroups.Clear();
            vm.BuildGroups(envelope.Result.CoreResult.Groups);
            matSw.Stop();
            materializationMs = matSw.ElapsedMilliseconds;
            vm.MatchStatisticsText = envelope.Result.CoreResult.Summary.SummaryText;
            vm.Title = $"Read-only Sandbox: {DiffTimelineSnapshotBrowserViewModel.ToShortHash(request.OldSnapshotHash)} -> {DiffTimelineSnapshotBrowserViewModel.ToShortHash(request.NewSnapshotHash)}";
            visibleItemsUpdateMs = openSw.ElapsedMilliseconds;

            var window = new ProjectDiffWindow(vm);
            var owner = System.Windows.Application.Current?.Windows.OfType<System.Windows.Window>().FirstOrDefault(x => x.IsActive);
            if (owner is not null && !ReferenceEquals(owner, window))
            {
                window.Owner = owner;
            }

            window.Show();
            openSw.Stop();
            logger.Info(
                $"RouteA readonly open perf: totalOpenMs={openSw.ElapsedMilliseconds}, snapshotResolveMs={snapshotResolveMs}, pipelineBuildMs={pipelineBuildMs}, viewModelCreateMs={viewModelCreateMs}, materializationMs={materializationMs}, visibleItemsUpdateMs={visibleItemsUpdateMs}, totalItemCount={envelope.Result.CoreResult.RowSet.Rows.Count}");
            TryWriteRouteARenderPerfDiagnostics(
                new RouteARenderPerfDiagnostics(
                    Timestamp: DateTimeOffset.Now,
                    MeasurementSource: "OpenRouteADetailViewerReadOnlySandbox",
                    ProcessStartTime: SafeGetStartTime(Process.GetCurrentProcess()),
                    ProcessUptimeMs: GetProcessUptimeMs(),
                    TotalOpenMs: openSw.ElapsedMilliseconds,
                    SnapshotResolveMs: snapshotResolveMs,
                    PipelineBuildMs: pipelineBuildMs,
                    ViewModelCreateMs: viewModelCreateMs,
                    MaterializationMs: materializationMs,
                    VisibleItemsUpdateMs: visibleItemsUpdateMs,
                    TotalItemCount: envelope.Result.CoreResult.RowSet.Rows.Count,
                    ProjectedItemCount: vm.TimelineViewModel.ProjectedItemCount,
                    VisibleItemCount: vm.TimelineViewModel.LastVisibleCount,
                    InitialRenderItemCap: vm.TimelineViewModel.InitialRenderItemCap,
                    InitialRenderCapApplied: vm.TimelineViewModel.InitialRenderCapApplied,
                    ProjectionReused: vm.TimelineViewModel.ProjectionReused,
                    ProjectionRebuilt: vm.TimelineViewModel.ProjectionRebuilt,
                    LastInvalidationReason: vm.TimelineViewModel.LastInvalidationReason.ToString(),
                    ProjectionStatusText: vm.TimelineViewModel.LatestDiagnosticsSnapshot?.Display.OptimizationStatusText ?? string.Empty,
                    ProcessMetrics: CaptureRelatedProcessMetrics(),
                    GpuEnvironmentMetrics: CaptureGpuEnvironmentMetrics()));
            return new RouteADetailPreviewOpenResult(true, true, false, string.Empty, true, "ReadOnlySandbox", request.SelectedCandidateId, request.OldSnapshotHash, request.NewSnapshotHash);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "OpenRouteADetailViewerReadOnlySandbox failed");
            return new RouteADetailPreviewOpenResult(true, false, true, ex.Message, false, "ReadOnlyDryRun", request.SelectedCandidateId, request.OldSnapshotHash, request.NewSnapshotHash);
        }
    }

    private static void TryWriteRouteARenderPerfDiagnostics(RouteARenderPerfDiagnostics data)
    {
        try
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "YukkuriMovieMaker_v4_Lite",
                "diagnostics");
            Directory.CreateDirectory(baseDir);
            var fileName = $"routea-render-perf-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            var path = Path.Combine(baseDir, fileName);
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch
        {
            // Keep viewer open flow resilient even when diagnostics write fails.
        }
    }

    private static IReadOnlyList<RouteAProcessMetric> CaptureRelatedProcessMetrics()
    {
        var now = DateTimeOffset.Now;
        var first = Process.GetProcesses()
            .Where(IsRelatedYmmProcess)
            .ToDictionary(
                p => p.Id,
                p => new { Process = p, Cpu = SafeGetCpu(p), Mem = SafeGetWorkingSet(p), Name = p.ProcessName, StartTime = SafeGetStartTime(p) });

        Thread.Sleep(250);

        var second = Process.GetProcesses()
            .Where(IsRelatedYmmProcess)
            .ToDictionary(
                p => p.Id,
                p => new { Process = p, Cpu = SafeGetCpu(p), Mem = SafeGetWorkingSet(p), Name = p.ProcessName, StartTime = SafeGetStartTime(p), Path = SafeGetMainModulePath(p) });

        var cpuScale = 100.0 / (Environment.ProcessorCount * 0.25);
        var gpuUsageByPid = CaptureGpuUsageByProcess();
        var list = new List<RouteAProcessMetric>();
        foreach (var kv in second)
        {
            first.TryGetValue(kv.Key, out var prev);
            var cpuDeltaMs = (kv.Value.Cpu - (prev?.Cpu ?? kv.Value.Cpu)).TotalMilliseconds;
            var cpuPercent = Math.Max(0, cpuDeltaMs * cpuScale);
            list.Add(new RouteAProcessMetric(
                Timestamp: now,
                ProcessId: kv.Key,
                ProcessName: kv.Value.Name,
                MainModulePath: kv.Value.Path,
                StartTime: kv.Value.StartTime,
                WorkingSetBytes: kv.Value.Mem,
                CpuPercentApprox: Math.Round(cpuPercent, 2),
                GpuPercentApprox: Math.Round(gpuUsageByPid.GetValueOrDefault(kv.Key), 2)));
        }

        return list.OrderByDescending(x => x.WorkingSetBytes).ToList();
    }

    private static Dictionary<int, double> CaptureGpuUsageByProcess()
    {
        var result = new Dictionary<int, double>();
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            var instanceNames = category.GetInstanceNames();
            var counters = new List<(int Pid, PerformanceCounter Counter)>();
            foreach (var instance in instanceNames)
            {
                var pidIndex = instance.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
                if (pidIndex < 0)
                {
                    continue;
                }

                var pidTokenStart = pidIndex + 4;
                var pidTokenEnd = instance.IndexOf('_', pidTokenStart);
                var pidText = pidTokenEnd > pidTokenStart
                    ? instance[pidTokenStart..pidTokenEnd]
                    : instance[pidTokenStart..];
                if (!int.TryParse(pidText, out var pid))
                {
                    continue;
                }

                var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, readOnly: true);
                _ = counter.NextValue();
                counters.Add((pid, counter));
            }

            Thread.Sleep(180);
            foreach (var item in counters)
            {
                var value = item.Counter.NextValue();
                if (!result.TryAdd(item.Pid, value))
                {
                    result[item.Pid] += value;
                }
            }
        }
        catch
        {
            // Ignore GPU counter errors on unsupported environments.
        }

        return result;
    }

    private static RouteAGpuEnvironmentMetrics CaptureGpuEnvironmentMetrics()
    {
        var tierRaw = RenderCapability.Tier;
        var tierLevel = tierRaw >> 16;
        var supportsHardware = tierLevel > 0;
        var primary = CapturePrimaryVideoControllerInfo();
        return new RouteAGpuEnvironmentMetrics(
            WpfRenderTierRaw: tierRaw,
            WpfRenderTierLevel: tierLevel,
            WpfHardwareRenderingAvailable: supportsHardware,
            PrimaryGpuName: primary.Name,
            DriverVersion: primary.DriverVersion,
            DedicatedVramBytes: primary.DedicatedVramBytes,
            SharedVramBytes: primary.SharedVramBytes);
    }

    private static (string Name, string DriverVersion, long DedicatedVramBytes, long SharedVramBytes) CapturePrimaryVideoControllerInfo()
    {
        // Keep this dependency-free for no-deploy builds.
        return (string.Empty, string.Empty, 0, 0);
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
        return !string.IsNullOrWhiteSpace(path) &&
               path.Contains("YukkuriMovieMaker", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan SafeGetCpu(Process p)
    {
        try { return p.TotalProcessorTime; } catch { return TimeSpan.Zero; }
    }

    private static long SafeGetWorkingSet(Process p)
    {
        try { return p.WorkingSet64; } catch { return 0; }
    }

    private static DateTimeOffset? SafeGetStartTime(Process p)
    {
        try { return p.StartTime; } catch { return null; }
    }

    private static long GetProcessUptimeMs()
    {
        try
        {
            var start = Process.GetCurrentProcess().StartTime;
            return Math.Max(0, (long)(DateTime.Now - start).TotalMilliseconds);
        }
        catch
        {
            return 0;
        }
    }

    private static string SafeGetMainModulePath(Process p)
    {
        try { return p.MainModule?.FileName ?? string.Empty; } catch { return string.Empty; }
    }

    private sealed record RouteAProcessMetric(
        DateTimeOffset Timestamp,
        int ProcessId,
        string ProcessName,
        string MainModulePath,
        DateTimeOffset? StartTime,
        long WorkingSetBytes,
        double CpuPercentApprox,
        double GpuPercentApprox);

    private sealed record RouteAGpuEnvironmentMetrics(
        int WpfRenderTierRaw,
        int WpfRenderTierLevel,
        bool WpfHardwareRenderingAvailable,
        string PrimaryGpuName,
        string DriverVersion,
        long DedicatedVramBytes,
        long SharedVramBytes);

    private sealed record RouteARenderPerfDiagnostics(
        DateTimeOffset Timestamp,
        string MeasurementSource,
        DateTimeOffset? ProcessStartTime,
        long ProcessUptimeMs,
        long TotalOpenMs,
        long SnapshotResolveMs,
        long PipelineBuildMs,
        long ViewModelCreateMs,
        long MaterializationMs,
        long VisibleItemsUpdateMs,
        int TotalItemCount,
        int ProjectedItemCount,
        int VisibleItemCount,
        int InitialRenderItemCap,
        bool InitialRenderCapApplied,
        bool ProjectionReused,
        bool ProjectionRebuilt,
        string LastInvalidationReason,
        string ProjectionStatusText,
        IReadOnlyList<RouteAProcessMetric> ProcessMetrics,
        RouteAGpuEnvironmentMetrics GpuEnvironmentMetrics);

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
            var previewWorkspaceState = BuildCurrentPreviewWorkspaceState();
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
                comparisonHistory: comparisonHistoryStore.Load(),
                previewWorkspaceState: previewWorkspaceState);
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
        sw.Stop();
        lastFilterDuration = sw.Elapsed;
        var groupingSw = System.Diagnostics.Stopwatch.StartNew();
        latestGroupStates = ResolveGroupStates(latestCoreResult, selectedGroupingMode);
        groupingSw.Stop();
        lastGroupingDuration = groupingSw.Elapsed;
        sw.Stop();
        lastRenderDuration = lastFilterDuration + lastGroupingDuration;
        ResetRowWindow();
        NotifyMetricsRefreshCompleted(includeFilterState: true);
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

    public string SnapshotCompareSummaryText => SnapshotBrowser.CompareSummaryText;

    private DiffTimelinePreviewWorkspaceState BuildCurrentPreviewWorkspaceState()
    {
        // Always build from the latest in-memory VM state so export/validation paths use
        // the same snapshot source right before serialization.
        return BuildPreviewWorkspaceState();
    }

    private DiffTimelinePreviewWorkspaceState BuildPreviewWorkspaceState()
    {
        var filterState = new DiffTimelineFilterState(
            PathFilters: selectedPathFilters.ToList(),
            SemanticCategoryFilters: selectedSemanticCategories.ToList(),
            ChangeTypeFilters: selectedChangeTypes.ToList(),
            GroupFilters: selectedGroupFilters.ToList(),
            SearchQuery: new DiffTimelineSearchQuery(FilterSearchText, false, false),
            ChangedOnly: ChangedOnlyFilter,
            WarningOnly: WarningOnlyFilter);
        var metricsSnapshot = BuildMetricsSnapshot();
        return DiffTimelinePreviewWorkspaceStateBuilder.Build(
            filterState,
            SelectedGroupingMode,
            SnapshotBrowserStateForExport(),
            SnapshotBrowser.SelectedSession,
            SnapshotBrowser.LastCompareResultSummary,
            LatestManualValidationLogPath,
            SnapshotBrowser.LastCompareDiagnosticsPath,
            metricsSnapshot,
            SnapshotBrowser.LastCompareErrorText);
    }

    private DiffTimelineMetricsSnapshot BuildMetricsSnapshot()
    {
        var virtualizationState = BuildVirtualizationState();
        var heavyDiagnostics = BuildHeavyProjectDiagnostics();
        return DiffTimelineMetricsSnapshotBuilder.Build(
            lastRenderDuration,
            lastFilterDuration,
            lastGroupingDuration,
            lastCompareApplyDuration,
            lastUiUpdateDuration,
            virtualizationState,
            heavyDiagnostics,
            latestProjectionCacheStats,
            IsLargeResultMode,
            LargeResultModeReason,
            MaterializedRowLimit,
            TotalAvailableRowCount,
            DisplayedRowCount,
            DeferredRowCount,
            VisibleRowWindowStart,
            VisibleRowWindowSize,
            CanLoadMoreRows);
    }

    private static IReadOnlyList<string> BuildMetricsSnapshotConsistencyWarnings(DiffTimelineMetricsSnapshot snapshot)
    {
        var warnings = new List<string>();
        if (snapshot.DisplayedRowCount > snapshot.TotalAvailableRowCount)
        {
            warnings.Add("displayed-gt-total");
        }

        if (snapshot.DeferredRowCount < 0)
        {
            warnings.Add("deferred-negative");
        }

        if (snapshot.CanLoadMoreRows && snapshot.DeferredRowCount == 0)
        {
            warnings.Add("load-more-without-deferred");
        }

        if (!snapshot.IsLargeResultMode && snapshot.DeferredRowCount > 0)
        {
            warnings.Add("deferred-without-large-mode");
        }

        if (snapshot.TotalAvailableRowCount > 0 && snapshot.ProjectionCacheStats is null)
        {
            warnings.Add("missing-projection-cache-stats");
        }

        return warnings;
    }

    private IReadOnlyList<DiffTimelineLightweightRowProjection> GetLightweightProjections(DiffTimelineCoreResult coreResult)
    {
        var totalRows = coreResult.RowSet.Rows.Count;
        var materializeLimit = totalRows > 3000 ? 800 : totalRows > 1500 ? 1200 : totalRows;
        var cacheKey = string.Join("|",
            coreResult.Summary.BuildOptionsSnapshot.GetValueOrDefault("oldSnapshotHash") ?? "old",
            coreResult.Summary.BuildOptionsSnapshot.GetValueOrDefault("newSnapshotHash") ?? "new",
            totalRows.ToString(),
            materializeLimit.ToString());

        var list = rowProjectionCache.GetOrCreate(cacheKey, () =>
        {
            var rows = coreResult.RowSet.Rows;
            var projected = new List<DiffTimelineLightweightRowProjection>(materializeLimit);
            for (var i = 0; i < materializeLimit; i++)
            {
                var row = rows[i];
                projected.Add(new DiffTimelineLightweightRowProjection(
                    Id: row.RowId,
                    Kind: row.DiffKind,
                    Scope: row.Path,
                    Field: row.Field,
                    Before: row.OldValue,
                    After: row.NewValue,
                    TimelineIndex: row.TimelineIndex,
                    Layer: row.Layer,
                    Frame: row.Frame,
                    Length: row.Length,
                    DisplayText: $"{row.Path} {row.Field}",
                    ShortDisplayText: row.DisplayLabel,
                    GroupKey: row.GroupKey,
                    CachedSearchText: $"{row.Title} {row.Subtitle} {row.Detail}",
                    CachedFilterText: $"{row.DiffKind}|{row.SemanticCategory}|{row.FilterKey}",
                    Flags: row.DiffKind));
            }

            return projected;
        }, out _);

        latestProjectionCacheStats = rowProjectionCache.BuildStats(
            materializedRowCount: list.Count,
            totalRowCount: totalRows,
            deferredGroupCount: Math.Max(0, coreResult.Groups.Count - 100));
        totalAvailableRowCount = totalRows;
        materializedRowLimit = materializeLimit;
        isLargeResultMode = totalRows > materializeLimit;
        largeResultModeReason = isLargeResultMode ? $"row-count-threshold ({totalRows:N0} > {materializeLimit:N0})" : "none";
        return list;
    }

    private void MaterializeRowsForCurrentWindow(DiffTimelineCoreResult coreResult)
    {
        latestLightweightRows = GetLightweightProjections(coreResult);
        YmmDiffEntries.Clear();
        var take = Math.Min(visibleRowWindowSize, latestLightweightRows.Count);
        for (var i = visibleRowWindowStart; i < take; i++)
        {
            var row = latestLightweightRows[i];
            YmmDiffEntries.Add(new DiffEntryViewModel
            {
                Id = row.Id,
                Kind = row.Kind,
                Scope = row.Scope,
                Field = row.Field,
                Before = row.Before,
                After = row.After,
                TimelineIndex = row.TimelineIndex,
                Layer = row.Layer,
                Frame = row.Frame,
                Length = row.Length,
            });
        }

        displayedRowCount = YmmDiffEntries.Count;
        deferredRowCount = Math.Max(0, totalAvailableRowCount - displayedRowCount);
    }


    private DiffTimelineVirtualizationState BuildVirtualizationState()
    {
        var rowCount = latestCoreResult?.RowSet.Rows.Count ?? 0;
        var groupCount = latestCoreResult?.Groups.Count ?? 0;
        var expandedGroupCount = Math.Max(0, groupCount - latestGroupStates.Count(x => x.Collapsed));
        var visibleRowEstimate = latestFilteredResult?.MatchedRowCount ?? rowCount;
        var estimatedVisualCount = visibleRowEstimate + expandedGroupCount;
        var estimatedMemoryUsage = (long)rowCount * 320L + (long)groupCount * 160L;
        var recommend = rowCount > 1500 || groupCount > 120 || estimatedVisualCount > 1800;
        return new DiffTimelineVirtualizationState(
            RowCount: rowCount,
            VisibleRowEstimate: visibleRowEstimate,
            GroupCount: groupCount,
            ExpandedGroupCount: expandedGroupCount,
            EstimatedVisualCount: estimatedVisualCount,
            EstimatedMemoryUsageBytes: estimatedMemoryUsage,
            VirtualizationRecommended: recommend);
    }

    private DiffTimelineHeavyProjectDiagnostics BuildHeavyProjectDiagnostics()
    {
        var state = BuildVirtualizationState();
        var reasons = new List<string>();
        if (state.RowCount > 1500) reasons.Add("row-count-threshold");
        if (state.GroupCount > 120) reasons.Add("group-count-threshold");
        if (state.EstimatedVisualCount > 1800) reasons.Add("visual-count-threshold");
        return new DiffTimelineHeavyProjectDiagnostics(
            HeavyProjectDetected: reasons.Count > 0,
            VirtualizationRecommended: state.VirtualizationRecommended,
            Reasons: reasons);
    }
    private void RefreshReusableSessionState()
    {
        var sessions = reusableSessionStore.LatestSessions(20);
        var persisted = reusableSessionStore.LoadPersistedSnapshots();
        SnapshotBrowser.ApplyPersistedState(persisted, sessions);
        LatestReusableSessionSummary = sessions.Count == 0 ? "no reusable sessions" : $"sessions={sessions.Count}";
        LatestReusableSessionPath = Path.Combine(AppContext.BaseDirectory, "diagnostics", "difftimeline-reusable-compare-sessions.json");
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

