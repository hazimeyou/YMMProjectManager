namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelinePreviewWorkspaceState(
    DiffTimelineFilterState? FilterState,
    string GroupingMode,
    DiffTimelineSnapshotBrowserState SnapshotBrowserState,
    DiffTimelineReusableCompareSession? SelectedCompareSession,
    string LatestCompareResultSummary,
    string LatestValidationLogPath,
    string LatestDiagnosticsPath,
    string LatestExportPath,
    IReadOnlyList<string> LatestWarnings,
    IReadOnlyList<string> LatestErrors);
