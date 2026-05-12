using YMMProjectManager.Presentation.ViewModels;

namespace YMMProjectManager.Presentation.TimelinePresentation.State;

internal sealed record DiffTimelineMetricsSnapshot(
    DiffTimelineRenderMetrics RenderMetrics,
    DiffTimelineVirtualizationState VirtualizationState,
    DiffTimelineHeavyProjectDiagnostics HeavyProjectDiagnostics,
    DiffTimelineProjectionCacheStats? ProjectionCacheStats,
    bool IsLargeResultMode,
    string LargeResultModeReason,
    int MaterializedRowLimit,
    int TotalAvailableRowCount,
    int DisplayedRowCount,
    int DeferredRowCount,
    int VisibleRowWindowStart,
    int VisibleRowWindowSize,
    bool CanLoadMoreRows);
