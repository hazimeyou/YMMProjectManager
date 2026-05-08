
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
                commitHash: "99dff2c");
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
        if (string.IsNullOrWhiteSpace(oldProjectPath) || string.IsNullOrWhiteSpace(newProjectPath))
        {
            return null;
        }

        if (!File.Exists(oldProjectPath) || !File.Exists(newProjectPath))
        {
            return null;
        }

        var adapter = new YmmNormalizedJsonSnapshotAdapter(message => logger.Info(message));
        var oldJson = normalizeService.NormalizeFileAsync(oldProjectPath).GetAwaiter().GetResult();
        var newJson = normalizeService.NormalizeFileAsync(newProjectPath).GetAwaiter().GetResult();
        var oldSnapshot = adapter.Convert("project-old", Path.GetFileNameWithoutExtension(oldProjectPath), oldProjectPath, oldJson);
        var newSnapshot = adapter.Convert("project-new", Path.GetFileNameWithoutExtension(newProjectPath), newProjectPath, newJson);
        return (oldSnapshot, newSnapshot);
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

