namespace YMMProjectManager.Presentation.Timeline.Services;

public enum ReadonlyTimelineRenderInvalidationReason
{
    None,
    ViewportChanged,
    ZoomChanged,
    HoverChanged,
    SelectionChanged,
    ProjectionOptionsChanged,
    FitTimelineChanged,
    HeavyModeChanged,
}

public sealed record ReadonlyTimelineProjectionDiff(
    int AddedItems,
    int RemovedItems,
    int RetainedItems,
    int UpdatedItems,
    bool ViewportChanged,
    bool ZoomChanged,
    bool SelectionChanged,
    bool HoverChanged);

public sealed record ReadonlyTimelineRenderInvalidationState(
    ReadonlyTimelineRenderInvalidationReason LastInvalidationReason,
    bool ProjectionReused,
    bool ProjectionRebuilt,
    int CachedProjectionHit,
    int CachedProjectionMiss);

public sealed record ReadonlyTimelineProjectionReuseState(
    bool ProjectionReused,
    string ReuseReason,
    ReadonlyTimelineProjectionDiff Diff,
    ReadonlyTimelineRenderInvalidationState Invalidation);
