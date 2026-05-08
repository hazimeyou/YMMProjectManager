namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineCoreRowBuilder
{
    public static DiffTimelineCoreRowSet BuildRows(DiffTimelineCoreResult coreResult)
    {
        var groupLabelByKey = coreResult.Groups
            .ToDictionary(x => x.GroupKey, x => x.GroupDisplayLabel, StringComparer.Ordinal);

        var rows = coreResult.Snapshot.Items
            .Select((item, index) => CreateRow(item, index, groupLabelByKey))
            .OrderBy(x => x.SortKey)
            .ThenBy(x => x.Order)
            .ToList();

        var groupCounts = rows
            .GroupBy(x => x.GroupKey)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);

        var semanticCounts = rows
            .GroupBy(x => x.SemanticCategory)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);

        return new DiffTimelineCoreRowSet(rows, groupCounts, semanticCounts);
    }

    private static DiffTimelineCoreRow CreateRow(
        DiffTimelineCoreItem item,
        int order,
        IReadOnlyDictionary<string, string> groupLabelByKey)
    {
        var groupDisplayLabel = groupLabelByKey.TryGetValue(item.GroupKey, out var label)
            ? label
            : DiffTimelineGroupResolver.ResolveGroupDisplayLabelByKey(item.GroupKey);

        var subtitle = $"{item.PathLabel} | T{item.TimelineIndex}/L{item.Layer}";
        var detail = $"{item.OldValue} -> {item.NewValue}";

        var metadata = new Dictionary<string, string>(item.DiagnosticsMetadata, StringComparer.Ordinal)
        {
            ["rowOrder"] = order.ToString(),
            ["groupDisplayLabel"] = groupDisplayLabel,
        };

        return new DiffTimelineCoreRow(
            RowId: item.Id,
            SourceItemId: item.Id,
            Title: item.DisplayLabel,
            Subtitle: subtitle,
            Detail: detail,
            DisplayLabel: item.DisplayLabel,
            OldValue: item.OldValue,
            NewValue: item.NewValue,
            GroupKey: item.GroupKey,
            GroupDisplayLabel: groupDisplayLabel,
            FilterKey: item.FilterKey,
            SemanticCategory: item.SemanticCategory,
            DiffKind: item.KindLabel,
            Path: item.PathLabel,
            Field: item.FieldLabel,
            TimelineIndex: item.TimelineIndex,
            Layer: item.Layer,
            Frame: item.Frame,
            Length: item.Length,
            SortKey: ComposeSortKey(item),
            Order: order,
            DiagnosticsMetadata: metadata);
    }

    private static long ComposeSortKey(DiffTimelineCoreItem item)
    {
        var timeline = (long)Math.Max(0, item.TimelineIndex);
        var layer = (long)Math.Max(0, item.Layer);
        var frame = (long)Math.Max(0, item.Frame);
        return (timeline << 40) | (layer << 20) | frame;
    }
}
