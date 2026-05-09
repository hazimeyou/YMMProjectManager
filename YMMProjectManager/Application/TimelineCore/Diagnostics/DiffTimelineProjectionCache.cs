namespace YMMProjectManager.Application.TimelineCore;

public sealed class DiffTimelineProjectionCache
{
    private readonly Dictionary<string, IReadOnlyList<DiffTimelineLightweightRowProjection>> cache = new(StringComparer.Ordinal);
    private int projectionReuseCount;

    public IReadOnlyList<DiffTimelineLightweightRowProjection> GetOrCreate(
        string cacheKey,
        Func<IReadOnlyList<DiffTimelineLightweightRowProjection>> factory,
        out bool cacheHit)
    {
        if (cache.TryGetValue(cacheKey, out var existing))
        {
            projectionReuseCount++;
            cacheHit = true;
            return existing;
        }

        var created = factory();
        cache[cacheKey] = created;
        cacheHit = false;
        return created;
    }

    public void InvalidateAll() => cache.Clear();

    public DiffTimelineProjectionCacheStats BuildStats(int materializedRowCount, int totalRowCount, int deferredGroupCount)
    {
        var deferred = Math.Max(0, totalRowCount - materializedRowCount);
        return new DiffTimelineProjectionCacheStats(
            CachedProjectionCount: cache.Count,
            MaterializedRowCount: materializedRowCount,
            ProjectionReuseCount: projectionReuseCount,
            DeferredProjectionCount: deferred,
            DeferredGroupCount: deferredGroupCount);
    }
}
