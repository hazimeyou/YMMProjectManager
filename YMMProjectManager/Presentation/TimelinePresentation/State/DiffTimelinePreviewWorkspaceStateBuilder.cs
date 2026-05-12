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
        DiffTimelineRenderMetrics renderMetrics,
        DiffTimelineVirtualizationState virtualizationState,
        DiffTimelineHeavyProjectDiagnostics heavyProjectDiagnostics,
        DiffTimelineProjectionCacheStats? projectionCacheStats,
        bool isLargeResultMode,
        string largeResultModeReason,
        int materializedRowLimit,
        int totalAvailableRowCount,
        int displayedRowCount,
        int deferredRowCount,
        int visibleRowWindowStart,
        int visibleRowWindowSize,
        bool canLoadMoreRows,
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
            RenderMetrics: renderMetrics,
            VirtualizationState: virtualizationState,
            HeavyProjectDiagnostics: heavyProjectDiagnostics,
            ProjectionCacheStats: projectionCacheStats,
            IsLargeResultMode: isLargeResultMode,
            LargeResultModeReason: largeResultModeReason,
            MaterializedRowLimit: materializedRowLimit,
            TotalAvailableRowCount: totalAvailableRowCount,
            DisplayedRowCount: displayedRowCount,
            DeferredRowCount: deferredRowCount,
            VisibleRowWindowStart: visibleRowWindowStart,
            VisibleRowWindowSize: visibleRowWindowSize,
            CanLoadMoreRows: canLoadMoreRows);
    }
}
