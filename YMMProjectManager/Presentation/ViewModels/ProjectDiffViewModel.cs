
using YMMProjectManager.Application.TimelineCore;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed class ProjectDiffViewModel : ViewModelBase, IDisposable
{
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
                    Kind = ToDiffKindLabel(x.Kind.ToString()),
                    Scope = x.Path,
                    Field = "JSON",
                    Before = DiffDisplayTextService.ToDisplayText(x.Before),
                    After = DiffDisplayTextService.ToDisplayText(x.After),
                });
            }

            var ymmResult = ymmDiffService.DiffWithStatistics(before, after);
            var timelineItems = new List<DiffTimelineItemViewModel>(ymmResult.Entries.Count);
            var coreSnapshot = DiffTimelineCoreBuilder.Build(
                ymmResult.Entries,
                kindLabel: x => ToDiffKindLabel(x),
                fieldLabel: x => ToFieldLabel(x),
                displayText: x => DiffDisplayTextService.ToDisplayText(x?.ToString()));
            for (var i = 0; i < ymmResult.Entries.Count; i++)
            {
                var x = ymmResult.Entries[i];
                var id = $"diff-{i}";
                YmmDiffEntries.Add(new DiffEntryViewModel
                {
                    Id = id,
                    Kind = ToDiffKindLabel(x.Kind.ToString()),
                    Scope = x.Scope,
                    Field = ToFieldLabel(x.Field),
                    Before = DiffDisplayTextService.ToDisplayText(x.Before),
                    After = DiffDisplayTextService.ToDisplayText(x.After),
                    TimelineIndex = x.TimelineIndex,
                    Layer = x.Layer,
                    Frame = x.Frame,
                    Length = x.Length,
                });

                var core = coreSnapshot.Items[i];
                timelineItems.Add(TimelineViewModel.CreateItem(
                    id: core.Id,
                    kind: core.KindLabel,
                    category: core.Category,
                    displayName: core.DisplayName,
                    timelineIndex: core.TimelineIndex,
                    layer: core.Layer,
                    frame: core.Frame,
                    length: core.Length,
                    oldValue: core.OldValue,
                    newValue: core.NewValue));
            }

            BuildGroups();
            MatchStatisticsText = FormatStatistics(ymmResult.Statistics);
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

    private void BuildGroups()
    {
        static string ResolveGroupName(DiffEntryViewModel item)
        {
            return item.Field switch
            {
                "テキスト" => "テキスト変更",
                "素材パス" => "素材パス変更",
                "フレーム" or "レイヤー" => "タイムライン移動",
                "長さ" => "長さ変更",
                _ => "その他",
            };
        }

        foreach (var group in YmmDiffEntries.GroupBy(ResolveGroupName).OrderByDescending(x => x.Count()))
        {
            DiffGroups.Add(new DiffGroupViewModel
            {
                GroupName = group.Key,
                Items = group.ToList(),
                Count = group.Count(),
            });
        }
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

    private static string FormatStatistics(YmmDiffMatchStatistics s)
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
            $"変更={s.ModifiedCount}");
    }

    private static string ToDiffKindLabel(string kind)
    {
        return kind switch
        {
            "Added" => "追加",
            "Removed" => "削除",
            "Moved" => "移動",
            "Changed" => "変更",
            _ => kind,
        };
    }

    private static string ToFieldLabel(object? field)
    {
        var value = field?.ToString() ?? string.Empty;
        return value switch
        {
            "Text" => "テキスト",
            "FilePath" => "素材パス",
            "Frame" => "フレーム",
            "Layer" => "レイヤー",
            "Length" => "長さ",
            _ => value,
        };
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

