namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelineRenderMetrics(
    TimeSpan LastRenderDuration,
    TimeSpan LastFilterDuration,
    TimeSpan LastGroupingDuration,
    TimeSpan LastCompareApplyDuration,
    TimeSpan LastUiUpdateDuration);

public sealed record DiffTimelineVirtualizationState(
    int RowCount,
    int VisibleRowEstimate,
    int GroupCount,
    int ExpandedGroupCount,
    int EstimatedVisualCount,
    long EstimatedMemoryUsageBytes,
    bool VirtualizationRecommended);

public sealed record DiffTimelineHeavyProjectDiagnostics(
    bool HeavyProjectDetected,
    bool VirtualizationRecommended,
    IReadOnlyList<string> Reasons);
