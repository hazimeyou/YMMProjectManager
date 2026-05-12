using YMMProjectManager.Presentation.ViewModels;

namespace YMMProjectManager.Presentation.TimelinePresentation.Display;

internal static class DiffTimelineCompareResultReflector
{
    public static IReadOnlyList<DiffTimelineItemViewModel> BuildTimelineItems(
        DiffTimelineCoreResult coreResult,
        DiffTimelineViewModel timelineViewModel)
    {
        var timelineItems = new List<DiffTimelineItemViewModel>(coreResult.RowSet.Rows.Count);
        for (var i = 0; i < coreResult.RowSet.Rows.Count; i++)
        {
            var row = coreResult.RowSet.Rows[i];
            timelineItems.Add(timelineViewModel.CreateItem(
                id: row.RowId,
                kind: row.DiffKind,
                category: row.SemanticCategory,
                displayName: row.DisplayLabel,
                timelineIndex: row.TimelineIndex,
                layer: row.Layer,
                frame: row.Frame,
                length: row.Length,
                oldValue: row.OldValue,
                newValue: row.NewValue));
        }

        return timelineItems;
    }

    public static string BuildManualCompareSummary(
        int addedCount,
        int removedCount,
        int changedCount,
        int rowCount,
        int groupCount,
        bool cacheHit)
        => $"added={addedCount}, removed={removedCount}, changed={changedCount}, rows={rowCount}, groups={groupCount}, cacheHit={cacheHit}";
}
