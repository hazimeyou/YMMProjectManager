using YMMProjectManager.Application.TimelineCore;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed class DiffTimelineSnapshotBrowserViewModel : ViewModelBase
{
    private DiffTimelineSnapshotListItem? selectedOldSnapshot;
    private DiffTimelineSnapshotListItem? selectedNewSnapshot;
    private string latestValidationState = "unknown";
    private string snapshotBrowserMessage = string.Empty;
    private bool isCompareRunning;
    private string lastCompareStatusText = "idle";
    private string lastCompareErrorText = string.Empty;
    private string lastCompareResultSummary = string.Empty;
    private string lastCompareDiagnosticsPath = string.Empty;
    private DateTimeOffset? lastCompareTimestamp;
    private string selectedSessionSummary = string.Empty;

    public ObservableCollection<DiffTimelineSnapshotListItem> SnapshotList { get; } = [];
    public ObservableCollection<DiffTimelineComparisonCandidate> ComparisonCandidates { get; } = [];
    public ObservableCollection<DiffTimelinePersistedSnapshotEntry> PersistedSnapshots { get; } = [];
    public ObservableCollection<DiffTimelineReusableCompareSession> LatestCompareSessions { get; } = [];
    public DiffTimelineReusableCompareSession? SelectedSession { get; set; }

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

    public string SnapshotBrowserMessage
    {
        get => snapshotBrowserMessage;
        private set => SetProperty(ref snapshotBrowserMessage, value);
    }

    public bool IsCompareRunning
    {
        get => isCompareRunning;
        set
        {
            if (SetProperty(ref isCompareRunning, value))
            {
                RefreshComputedState();
            }
        }
    }

    public string LastCompareStatusText
    {
        get => lastCompareStatusText;
        set => SetProperty(ref lastCompareStatusText, value);
    }

    public string LastCompareErrorText
    {
        get => lastCompareErrorText;
        set => SetProperty(ref lastCompareErrorText, value);
    }

    public string LastCompareResultSummary
    {
        get => lastCompareResultSummary;
        set => SetProperty(ref lastCompareResultSummary, value);
    }

    public string LastCompareDiagnosticsPath
    {
        get => lastCompareDiagnosticsPath;
        set => SetProperty(ref lastCompareDiagnosticsPath, value);
    }

    public DateTimeOffset? LastCompareTimestamp
    {
        get => lastCompareTimestamp;
        set => SetProperty(ref lastCompareTimestamp, value);
    }

    public string SelectedSessionSummary
    {
        get => selectedSessionSummary;
        set => SetProperty(ref selectedSessionSummary, value);
    }

    public bool CanCompare =>
        !IsCompareRunning &&
        SelectedOldSnapshot is not null &&
        SelectedNewSnapshot is not null &&
        !string.Equals(SelectedOldSnapshot.SnapshotHash, SelectedNewSnapshot.SnapshotHash, StringComparison.Ordinal);

    public string CompareSummaryText
    {
        get
        {
            if (SnapshotList.Count == 0)
            {
                return "No snapshots available.";
            }

            if (SelectedOldSnapshot is null || SelectedNewSnapshot is null)
            {
                return "Select both old and new snapshots.";
            }

            if (!CanCompare)
            {
                return IsCompareRunning
                    ? "Compare is running..."
                    : "Same snapshot cannot be compared.";
            }

            var oldHash = ToShortHash(SelectedOldSnapshot.SnapshotHash);
            var newHash = ToShortHash(SelectedNewSnapshot.SnapshotHash);
            return $"Compare: {SelectedOldSnapshot.SnapshotName} ({oldHash}) -> {SelectedNewSnapshot.SnapshotName} ({newHash})";
        }
    }

    public void SelectOldSnapshot(DiffTimelineSnapshotListItem? item)
    {
        SelectedOldSnapshot = item;
        RefreshComputedState();
    }

    public void SelectNewSnapshot(DiffTimelineSnapshotListItem? item)
    {
        SelectedNewSnapshot = item;
        RefreshComputedState();
    }

    public void SwapSelection()
    {
        (SelectedOldSnapshot, SelectedNewSnapshot) = (SelectedNewSnapshot, SelectedOldSnapshot);
        RefreshComputedState();
    }

    public void ClearSelection()
    {
        SelectedOldSnapshot = null;
        SelectedNewSnapshot = null;
        RefreshComputedState();
    }

    public DiffTimelineCompareRequest? BuildCompareRequest()
    {
        if (!CanCompare || SelectedOldSnapshot is null || SelectedNewSnapshot is null)
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
        SnapshotBrowserMessage = SnapshotList.Count == 0
            ? "Snapshot repository is empty."
            : "Preview mode: select snapshots and run manual compare.";
        RefreshComputedState();
    }

    public void ApplyPersistedState(
        IReadOnlyList<DiffTimelinePersistedSnapshotEntry> persistedSnapshots,
        IReadOnlyList<DiffTimelineReusableCompareSession> sessions)
    {
        PersistedSnapshots.Clear();
        foreach (var item in persistedSnapshots) PersistedSnapshots.Add(item);
        LatestCompareSessions.Clear();
        foreach (var s in sessions) LatestCompareSessions.Add(s);
        SelectedSessionSummary = SelectedSession is null
            ? "No session selected."
            : $"Session {SelectedSession.SessionId}: {ToShortHash(SelectedSession.OldSnapshotHash)} -> {ToShortHash(SelectedSession.NewSnapshotHash)}";
    }

    private void RefreshComputedState()
    {
        OnPropertyChanged(nameof(CanCompare));
        OnPropertyChanged(nameof(CompareSummaryText));
    }

    public static string ToShortHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return "(none)";
        }

        return hash.Length <= 8 ? hash : hash[..8];
    }
}
