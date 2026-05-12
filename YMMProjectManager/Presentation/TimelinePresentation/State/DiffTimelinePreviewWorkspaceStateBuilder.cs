using YMMProjectManager.Presentation.ViewModels;

namespace YMMProjectManager.Presentation.TimelinePresentation.State;

internal static class DiffTimelinePreviewWorkspaceStateBuilder
{
    public static DiffTimelinePreviewWorkspaceState Build(
        DiffTimelineFilterState filterState,
        string groupingMode,
        DiffTimelineSnapshotBrowserState snapshotBrowserState,
        DiffTimelineReusableCompareSession? selectedCompareSession,
        string latestCompareResultSummary,
        string latestValidationLogPath,
        string latestDiagnosticsPath,
        DiffTimelineMetricsSnapshot metricsSnapshot,
        string latestCompareErrorText)
    {
        return new DiffTimelinePreviewWorkspaceState(
            FilterState: filterState,
            GroupingMode: groupingMode,
            SnapshotBrowserState: snapshotBrowserState,
            SelectedCompareSession: selectedCompareSession,
            LatestCompareResultSummary: latestCompareResultSummary,
            LatestValidationLogPath: latestValidationLogPath,
            LatestDiagnosticsPath: latestDiagnosticsPath,
            LatestExportPath: string.Empty,
            LatestWarnings: [],
            LatestErrors: string.IsNullOrWhiteSpace(latestCompareErrorText) ? [] : [latestCompareErrorText],
            RenderMetrics: metricsSnapshot.RenderMetrics,
            VirtualizationState: metricsSnapshot.VirtualizationState,
            HeavyProjectDiagnostics: metricsSnapshot.HeavyProjectDiagnostics,
            ProjectionCacheStats: metricsSnapshot.ProjectionCacheStats,
            IsLargeResultMode: metricsSnapshot.IsLargeResultMode,
            LargeResultModeReason: metricsSnapshot.LargeResultModeReason,
            MaterializedRowLimit: metricsSnapshot.MaterializedRowLimit,
            TotalAvailableRowCount: metricsSnapshot.TotalAvailableRowCount,
            DisplayedRowCount: metricsSnapshot.DisplayedRowCount,
            DeferredRowCount: metricsSnapshot.DeferredRowCount,
            VisibleRowWindowStart: metricsSnapshot.VisibleRowWindowStart,
            VisibleRowWindowSize: metricsSnapshot.VisibleRowWindowSize,
            CanLoadMoreRows: metricsSnapshot.CanLoadMoreRows);
    }
}
