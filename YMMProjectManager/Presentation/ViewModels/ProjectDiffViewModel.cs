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

    public ObservableCollection<DiffEntryViewModel> JsonDiffEntries { get; } = [];
    public ObservableCollection<DiffEntryViewModel> YmmDiffEntries { get; } = [];
    public DiffTimelineViewModel TimelineViewModel { get; } = new();

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

            MatchStatisticsText = FormatStatistics(ymmResult.Statistics);
            TimelineViewModel.SetItems(timelineItems);
            SelectedYmmDiffEntry = YmmDiffEntries.FirstOrDefault();
        }
        catch (Exception ex)
        {
            logger.Error(ex, "ApplyDiff failed");
            MatchStatisticsText = "統計の計算に失敗しました。";
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
