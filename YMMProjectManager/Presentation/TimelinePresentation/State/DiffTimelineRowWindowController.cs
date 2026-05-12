using YMMProjectManager.Presentation.ViewModels;

namespace YMMProjectManager.Presentation.TimelinePresentation.State;

internal sealed class DiffTimelineRowWindowController
{
    private int visibleRowWindowStart;
    private int visibleRowWindowSize = 500;

    public int VisibleRowWindowStart => visibleRowWindowStart;
    public int VisibleRowWindowSize => visibleRowWindowSize;

    public int TotalAvailableRowCount { get; private set; }
    public int MaterializedRowLimit { get; private set; }
    public int DisplayedRowCount { get; private set; }
    public int DeferredRowCount { get; private set; }
    public bool IsLargeResultMode { get; private set; }
    public string LargeResultModeReason { get; private set; } = "none";
    public bool CanLoadMoreRows => DeferredRowCount > 0;
    public DiffTimelineProjectionCacheStats? LatestProjectionCacheStats { get; private set; }

    public void LoadMoreRows()
    {
        visibleRowWindowSize += Math.Max(250, Math.Max(1, MaterializedRowLimit) / 2);
    }

    public void ResetRowWindow()
    {
        visibleRowWindowStart = 0;
        visibleRowWindowSize = 500;
    }

    public IReadOnlyList<DiffTimelineLightweightRowProjection> BuildLightweightProjections(
        DiffTimelineCoreResult coreResult,
        DiffTimelineProjectionCache rowProjectionCache)
    {
        var totalRows = coreResult.RowSet.Rows.Count;
        var materializeLimit = totalRows > 3000 ? 800 : totalRows > 1500 ? 1200 : totalRows;
        var cacheKey = string.Join("|",
            coreResult.Summary.BuildOptionsSnapshot.GetValueOrDefault("oldSnapshotHash") ?? "old",
            coreResult.Summary.BuildOptionsSnapshot.GetValueOrDefault("newSnapshotHash") ?? "new",
            totalRows.ToString(),
            materializeLimit.ToString());

        var list = rowProjectionCache.GetOrCreate(cacheKey, () =>
        {
            var rows = coreResult.RowSet.Rows;
            var projected = new List<DiffTimelineLightweightRowProjection>(materializeLimit);
            for (var i = 0; i < materializeLimit; i++)
            {
                var row = rows[i];
                projected.Add(new DiffTimelineLightweightRowProjection(
                    Id: row.RowId,
                    Kind: row.DiffKind,
                    Scope: row.Path,
                    Field: row.Field,
                    Before: row.OldValue,
                    After: row.NewValue,
                    TimelineIndex: row.TimelineIndex,
                    Layer: row.Layer,
                    Frame: row.Frame,
                    Length: row.Length,
                    DisplayText: $"{row.Path} {row.Field}",
                    ShortDisplayText: row.DisplayLabel,
                    GroupKey: row.GroupKey,
                    CachedSearchText: $"{row.Title} {row.Subtitle} {row.Detail}",
                    CachedFilterText: $"{row.DiffKind}|{row.SemanticCategory}|{row.FilterKey}",
                    Flags: row.DiffKind));
            }

            return projected;
        }, out _);

        LatestProjectionCacheStats = rowProjectionCache.BuildStats(
            materializedRowCount: list.Count,
            totalRowCount: totalRows,
            deferredGroupCount: Math.Max(0, coreResult.Groups.Count - 100));
        TotalAvailableRowCount = totalRows;
        MaterializedRowLimit = materializeLimit;
        IsLargeResultMode = totalRows > materializeLimit;
        LargeResultModeReason = IsLargeResultMode ? $"row-count-threshold ({totalRows:N0} > {materializeLimit:N0})" : "none";
        return list;
    }

    public IReadOnlyList<DiffTimelineLightweightRowProjection> SliceForCurrentWindow(
        IReadOnlyList<DiffTimelineLightweightRowProjection> lightweightRows)
    {
        var take = Math.Min(visibleRowWindowSize, lightweightRows.Count);
        var result = new List<DiffTimelineLightweightRowProjection>(Math.Max(0, take - visibleRowWindowStart));
        for (var i = visibleRowWindowStart; i < take; i++)
        {
            result.Add(lightweightRows[i]);
        }

        DisplayedRowCount = result.Count;
        DeferredRowCount = Math.Max(0, TotalAvailableRowCount - DisplayedRowCount);
        return result;
    }
}
