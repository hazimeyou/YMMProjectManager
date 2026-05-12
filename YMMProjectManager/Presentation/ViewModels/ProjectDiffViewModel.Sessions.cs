namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectDiffViewModel
{
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

    public void SaveCurrentCompareSession()
    {
        var request = SnapshotBrowser.BuildCompareRequest();
        if (request is null)
        {
            TrackManualUiAction("SessionSaveFailed", "compare-request-missing");
            PersistManualValidationLog();
            return;
        }

        var session = new DiffTimelineReusableCompareSession(
            SessionId: Guid.NewGuid().ToString("N"),
            OldSnapshotHash: request.OldSnapshotHash,
            NewSnapshotHash: request.NewSnapshotHash,
            CompareOptions: request.CompareOptions,
            FilterState: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["searchText"] = FilterSearchText ?? string.Empty,
                ["changeType"] = SelectedChangeTypeFilter,
                ["semantic"] = SelectedSemanticCategoryFilter,
                ["path"] = SelectedPathFilter,
                ["group"] = SelectedGroupFilter,
            },
            GroupingMode: SelectedGroupingMode,
            CompareSummary: SnapshotBrowser.LastCompareResultSummary,
            LatestDiagnosticsPath: SnapshotBrowser.LastCompareDiagnosticsPath,
            LatestExportPath: string.Empty,
            LatestValidationLogPath: LatestManualValidationLogPath,
            CreatedAt: DateTimeOffset.Now,
            UpdatedAt: DateTimeOffset.Now);
        reusableSessionStore.SaveSession(session);
        TrackManualUiAction("SessionSaved", session.SessionId);
        RefreshReusableSessionState();
        PersistManualValidationLog();
    }

    public void RestoreSelectedCompareSession()
    {
        var session = SnapshotBrowser.SelectedSession;
        if (session is null)
        {
            SnapshotBrowser.SelectedSessionSummary = "Restore blocked: no session selected.";
            TrackManualUiAction("SessionRestoreFailed", "no-session-selected");
            PersistManualValidationLog();
            return;
        }

        var oldSnapshot = SnapshotBrowser.SnapshotList.FirstOrDefault(x => string.Equals(x.SnapshotHash, session.OldSnapshotHash, StringComparison.Ordinal));
        var newSnapshot = SnapshotBrowser.SnapshotList.FirstOrDefault(x => string.Equals(x.SnapshotHash, session.NewSnapshotHash, StringComparison.Ordinal));
        if (oldSnapshot is null || newSnapshot is null)
        {
            SnapshotBrowser.SelectedSessionSummary = "Restore failed: snapshot body/metadata missing for selected session.";
            TrackManualUiAction("SessionRestoreFailed", "snapshot-missing");
            PersistManualValidationLog();
            return;
        }

        SnapshotBrowser.SelectOldSnapshot(oldSnapshot);
        SnapshotBrowser.SelectNewSnapshot(newSnapshot);
        FilterSearchText = session.FilterState.GetValueOrDefault("searchText") ?? string.Empty;
        SelectedChangeTypeFilter = session.FilterState.GetValueOrDefault("changeType") ?? "All";
        SelectedSemanticCategoryFilter = session.FilterState.GetValueOrDefault("semantic") ?? "All";
        SelectedPathFilter = session.FilterState.GetValueOrDefault("path") ?? string.Empty;
        SelectedGroupFilter = session.FilterState.GetValueOrDefault("group") ?? string.Empty;
        SelectedGroupingMode = string.IsNullOrWhiteSpace(session.GroupingMode) ? "None" : session.GroupingMode;
        SnapshotBrowser.SelectedSessionSummary = $"Restored {session.SessionId}";
        TrackManualUiAction("SessionRestored", session.SessionId);
        PersistManualValidationLog();
    }

    public void OnSelectedCompareSessionChanged()
    {
        if (SnapshotBrowser.SelectedSession is null)
        {
            SnapshotBrowser.SelectedSessionSummary = "No session selected.";
            return;
        }

        var s = SnapshotBrowser.SelectedSession;
        SnapshotBrowser.SelectedSessionSummary = $"Session {s.SessionId}: {DiffTimelineSnapshotBrowserViewModel.ToShortHash(s.OldSnapshotHash)} -> {DiffTimelineSnapshotBrowserViewModel.ToShortHash(s.NewSnapshotHash)}";
        TrackManualUiAction("SessionSelected", s.SessionId);
        PersistManualValidationLog();
    }
}
