using YMMProjectManager.Application.TimelineCore;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed class DiffTimelineSnapshotBrowserViewModel : ViewModelBase
{
    private DiffTimelineSnapshotListItem? selectedOldSnapshot;
    private DiffTimelineSnapshotListItem? selectedNewSnapshot;
    private string latestValidationState = "unknown";

    public ObservableCollection<DiffTimelineSnapshotListItem> SnapshotList { get; } = [];
    public ObservableCollection<DiffTimelineComparisonCandidate> ComparisonCandidates { get; } = [];

    public DiffTimelineSnapshotListItem? SelectedOldSnapshot
    {
        get => selectedOldSnapshot;
        set => SetProperty(ref selectedOldSnapshot, value);
    }

    public DiffTimelineSnapshotListItem? SelectedNewSnapshot
    {
        get => selectedNewSnapshot;
        set => SetProperty(ref selectedNewSnapshot, value);
    }

    public string LatestValidationState
    {
        get => latestValidationState;
        set => SetProperty(ref latestValidationState, value);
    }

    public DiffTimelineCompareRequest? BuildCompareRequest()
    {
        if (SelectedOldSnapshot is null || SelectedNewSnapshot is null)
        {
            return null;
        }

        return new DiffTimelineCompareRequest(
            SelectedOldSnapshot.SnapshotHash,
            SelectedNewSnapshot.SnapshotHash,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    public void ApplyState(DiffTimelineSnapshotBrowserState state)
    {
        SnapshotList.Clear();
        foreach (var item in state.Snapshots)
        {
            SnapshotList.Add(item);
        }

        ComparisonCandidates.Clear();
        foreach (var candidate in state.ComparisonCandidates)
        {
            ComparisonCandidates.Add(candidate);
        }

        LatestValidationState = state.LatestValidationState;
    }
}
