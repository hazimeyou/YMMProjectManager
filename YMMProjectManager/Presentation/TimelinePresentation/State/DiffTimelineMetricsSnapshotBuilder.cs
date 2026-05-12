using YMMProjectManager.Presentation.ViewModels;

namespace YMMProjectManager.Presentation.TimelinePresentation.State;

internal static class DiffTimelineMetricsSnapshotBuilder
{
    public static DiffTimelineMetricsSnapshot Build(
        TimeSpan lastRenderDuration,
        TimeSpan lastFilterDuration,
        TimeSpan lastGroupingDuration,
        TimeSpan lastCompareApplyDuration,
        TimeSpan lastUiUpdateDuration,
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
        bool canLoadMoreRows)
    {
        return new DiffTimelineMetricsSnapshot(
            RenderMetrics: new DiffTimelineRenderMetrics(
                LastRenderDuration: lastRenderDuration,
                LastFilterDuration: lastFilterDuration,
                LastGroupingDuration: lastGroupingDuration,
                LastCompareApplyDuration: lastCompareApplyDuration,
                LastUiUpdateDuration: lastUiUpdateDuration),
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
