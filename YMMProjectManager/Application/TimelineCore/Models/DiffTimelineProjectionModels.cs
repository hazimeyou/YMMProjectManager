namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelineLightweightRowProjection(
    string Id,
    string Kind,
    string Scope,
    string Field,
    string Before,
    string After,
    int TimelineIndex,
    int Layer,
    int Frame,
    int Length,
    string DisplayText,
    string ShortDisplayText,
    string GroupKey,
    string CachedSearchText,
    string CachedFilterText,
    string Flags);

public sealed record DiffTimelineProjectionCacheStats(
    int CachedProjectionCount,
    int MaterializedRowCount,
    int ProjectionReuseCount,
    int DeferredProjectionCount,
    int DeferredGroupCount);
