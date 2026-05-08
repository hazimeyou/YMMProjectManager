namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineSummaryBuilder
{
    public static DiffTimelineCoreSummary Build(
        IReadOnlyList<DiffTimelineCoreItem> allItems,
        IReadOnlyList<DiffTimelineCoreItem> filteredItems,
        IReadOnlyList<DiffTimelineCoreGroup> groups,
        IReadOnlyDictionary<string, string> optionSnapshot)
    {
        var semanticCounts = filteredItems
            .GroupBy(x => x.SemanticCategory)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);

        var added = filteredItems.Count(x => string.Equals(x.KindLabel, "追加", StringComparison.Ordinal));
        var removed = filteredItems.Count(x => string.Equals(x.KindLabel, "削除", StringComparison.Ordinal));
        var changed = filteredItems.Count(x => string.Equals(x.KindLabel, "変更", StringComparison.Ordinal));
        var moved = filteredItems.Count(x => string.Equals(x.KindLabel, "移動", StringComparison.Ordinal));

        var summaryText = $"items={filteredItems.Count}/{allItems.Count}, groups={groups.Count}, +{added}/-{removed}/~{changed}/>{moved}";
        return new DiffTimelineCoreSummary(
            TotalItemCount: allItems.Count,
            FilteredItemCount: filteredItems.Count,
            GroupCount: groups.Count,
            AddedCount: added,
            RemovedCount: removed,
            ChangedCount: changed,
            MovedCount: moved,
            SemanticCategoryCounts: semanticCounts,
            BuildOptionsSnapshot: new Dictionary<string, string>(optionSnapshot, StringComparer.Ordinal),
            SummaryText: summaryText);
    }
}
