using System.Collections.ObjectModel;
using System.IO;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Diff;
using YMMProjectManager.Infrastructure.History;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed class ProjectDiffViewModel : ViewModelBase
{
    private readonly FileLogger logger;
    private readonly ProjectSnapshotService snapshotService;
    private readonly JsonNormalizeService normalizeService;
    private readonly JsonDiffService jsonDiffService;
    private readonly YmmProjectDiffService ymmDiffService;

    private string title = "差分";
    private string matchStatisticsText = string.Empty;
    private DiffEntryViewModel? selectedYmmDiffEntry;
    private bool isSyncingSelection;
    private int pureTimelineCurrentFrame;
    private string pureTimelineSelection = "(none)";
    private string pureTimelineStatus = "Placeholder";
    private string pureTimelineActiveScene = "Scene: (placeholder)";
    private string pureTimelineActiveTimeline = "Timeline: (placeholder)";
    private string lastSyncAction = "Last Sync: (none)";
    private TimelineSyncState selectedSyncState = TimelineSyncState.Detached;
    private TimelineMode selectedTimelineMode = TimelineMode.Synced;

    public ObservableCollection<DiffEntryViewModel> JsonDiffEntries { get; } = [];
    public ObservableCollection<DiffEntryViewModel> YmmDiffEntries { get; } = [];
    public ObservableCollection<DiffGroupViewModel> DiffGroups { get; } = [];
    public DiffTimelineViewModel TimelineViewModel { get; } = new();

    public IReadOnlyList<TimelineSyncState> SyncStateOptions { get; } = Enum.GetValues<TimelineSyncState>();
    public IReadOnlyList<TimelineMode> TimelineModeOptions { get; } = Enum.GetValues<TimelineMode>();

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
        get => pureTimelineCurrentFrame;
        set
        {
            if (SetProperty(ref pureTimelineCurrentFrame, Math.Max(0, value)))
            {
                TimelineViewModel.SetCurrentFrame(pureTimelineCurrentFrame);
            }
        }
    }

    public string PureTimelineSelection
    {
        get => pureTimelineSelection;
        set => SetProperty(ref pureTimelineSelection, value);
    }

    public string PureTimelineStatus
    {
        get => pureTimelineStatus;
        set => SetProperty(ref pureTimelineStatus, value);
    }

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
        get => lastSyncAction;
        set => SetProperty(ref lastSyncAction, value);
    }

    public TimelineSyncState SelectedSyncState
    {
        get => selectedSyncState;
        set
        {
            if (SetProperty(ref selectedSyncState, value))
            {
                ApplySyncModeAndState();
                LastSyncAction = $"Last Sync: Set state {value}";
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
                LastSyncAction = $"Last Sync: Set mode {value}";
            }
        }
    }

    public string PureTimelineSyncState => TimelineViewModel.SyncState.ToString();
    public string PureTimelineMode => TimelineViewModel.Mode.ToString();

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

        TimelineViewModel.SelectedDiffItemChanged += OnTimelineSelectedDiffItemChanged;
        ApplySyncModeAndState();
    }

    public void SyncFrameFromPlaceholder()
    {
        TimelineViewModel.SetCurrentFrame(PureTimelineCurrentFrame);
        LastSyncAction = $"Last Sync: Synced frame {PureTimelineCurrentFrame}";
    }

    public void GoToCurrentFrame()
    {
        TimelineViewModel.ScrollToCurrentFrame();
        LastSyncAction = $"Last Sync: Go To Current Frame {PureTimelineCurrentFrame}";
    }

    public void CenterCurrentFrame()
    {
        TimelineViewModel.CenterCurrentFrame();
        LastSyncAction = $"Last Sync: Centered DiffTL at frame {PureTimelineCurrentFrame}";
    }

    public void SelectNearestDiffToCurrentFrame()
    {
        var ok = TimelineViewModel.SelectNearestDiffToCurrentFrame();
        LastSyncAction = ok
            ? $"Last Sync: Selected nearest diff at frame {PureTimelineCurrentFrame}"
            : "Last Sync: No diff for nearest selection";
    }

    public void JumpToFirstDiff()
    {
        var ok = TimelineViewModel.JumpToFirstDiff();
        if (ok && TimelineViewModel.SelectedDiffItem is not null)
        {
            PureTimelineCurrentFrame = TimelineViewModel.SelectedDiffItem.Frame;
        }

        LastSyncAction = ok ? "Last Sync: Jumped to first diff" : "Last Sync: No first diff";
    }

    public void JumpToLastDiff()
    {
        var ok = TimelineViewModel.JumpToLastDiff();
        if (ok && TimelineViewModel.SelectedDiffItem is not null)
        {
            PureTimelineCurrentFrame = TimelineViewModel.SelectedDiffItem.Frame;
        }

        LastSyncAction = ok ? "Last Sync: Jumped to last diff" : "Last Sync: No last diff";
    }

    public void JumpToPreviousDiffFromCurrentFrame()
    {
        var ok = TimelineViewModel.JumpToPreviousDiffFromCurrentFrame();
        if (ok && TimelineViewModel.SelectedDiffItem is not null)
        {
            PureTimelineCurrentFrame = TimelineViewModel.SelectedDiffItem.Frame;
        }

        LastSyncAction = ok ? "Last Sync: Jumped to previous diff from frame" : "Last Sync: No previous diff";
    }

    public void JumpToNextDiffFromCurrentFrame()
    {
        var ok = TimelineViewModel.JumpToNextDiffFromCurrentFrame();
        if (ok && TimelineViewModel.SelectedDiffItem is not null)
        {
            PureTimelineCurrentFrame = TimelineViewModel.SelectedDiffItem.Frame;
        }

        LastSyncAction = ok ? "Last Sync: Jumped to next diff from frame" : "Last Sync: No next diff";
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
        Title = $"差分: {snapshotId} -> current";
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
                    Kind = x.Kind.ToString(),
                    Scope = x.Path,
                    Field = "JSON",
                    Before = x.Before ?? string.Empty,
                    After = x.After ?? string.Empty,
                });
            }

            var ymmResult = ymmDiffService.DiffWithStatistics(before, after);
            var timelineItems = new List<DiffTimelineItemViewModel>(ymmResult.Entries.Count);
            for (var i = 0; i < ymmResult.Entries.Count; i++)
            {
                var x = ymmResult.Entries[i];
                var id = $"diff-{i}";
                YmmDiffEntries.Add(new DiffEntryViewModel
                {
                    Id = id,
                    Kind = x.Kind.ToString(),
                    Scope = x.Scope,
                    Field = x.Field,
                    Before = x.Before ?? string.Empty,
                    After = x.After ?? string.Empty,
                    TimelineIndex = x.TimelineIndex,
                    Layer = x.Layer,
                    Frame = x.Frame,
                    Length = x.Length,
                });

                timelineItems.Add(TimelineViewModel.CreateItem(
                    id: id,
                    kind: x.Kind.ToString(),
                    category: x.Category,
                    displayName: $"{x.Kind} {x.Field}",
                    timelineIndex: x.TimelineIndex,
                    layer: x.Layer,
                    frame: x.Frame,
                    length: Math.Max(1, x.Length),
                    oldValue: x.Before,
                    newValue: x.After));
            }

            BuildGroups();
            MatchStatisticsText = FormatStatistics(ymmResult.Statistics);
            TimelineViewModel.SetItems(timelineItems);
            SelectedYmmDiffEntry = YmmDiffEntries.FirstOrDefault();
            SelectedSyncState = TimelineSyncState.Synced;
            LastSyncAction = "Last Sync: Diff loaded and synced";
        }
        catch (Exception ex)
        {
            logger.Error(ex, "ApplyDiff failed");
            MatchStatisticsText = "統計の計算に失敗しました。";
            SelectedSyncState = TimelineSyncState.Error;
            LastSyncAction = "Last Sync: Error";
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
                "Text" => "Text Changes",
                "FilePath" => "FilePath Changes",
                "Frame" or "Layer" => "Timeline Moves",
                "Length" => "Length Changes",
                _ => "Other Changes",
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
            $"old={s.OldItemCount}",
            $"new={s.NewItemCount}",
            $"idMatch={s.MatchedByInternalId}",
            $"fallbackMatch={s.MatchedByFallback}",
            $"unmatchedOld={s.UnmatchedOldItems}",
            $"unmatchedNew={s.UnmatchedNewItems}",
            $"added={s.AddedCount}",
            $"removed={s.RemovedCount}",
            $"moved={s.MovedCount}",
            $"modified={s.ModifiedCount}");
    }
}
