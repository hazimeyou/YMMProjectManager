namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineGroupingUxResolver
{
    public static IReadOnlyList<DiffTimelineGroupState> BuildGroupStates(
        DiffTimelineCoreResult result,
        string mode = "semantic")
    {
        var groups = mode switch
        {
            "timeline" => result.RowSet.Rows.GroupBy(x => $"timeline:{x.TimelineIndex}"),
            "layer" => result.RowSet.Rows.GroupBy(x => $"layer:{x.Layer}"),
            "field" => result.RowSet.Rows.GroupBy(x => $"field:{x.Field}"),
            "path" => result.RowSet.Rows.GroupBy(x => $"path:{x.Path}"),
            "changeType" => result.RowSet.Rows.GroupBy(x => $"kind:{x.DiffKind}"),
            _ => result.RowSet.Rows.GroupBy(x => $"semantic:{x.SemanticCategory}"),
        };

        return groups.Select(g => new DiffTimelineGroupState(
            GroupKey: g.Key,
            GroupDisplayLabel: g.Key,
            Collapsed: false,
            RowCount: g.Count(),
            SemanticSummary: g.GroupBy(x => x.SemanticCategory).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal),
            SeveritySummary: g.GroupBy(ResolveSeverity).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal)))
            .OrderByDescending(x => x.RowCount)
            .ToList();
    }

    public static DiffTimelineSemanticUxMetadata BuildSemanticUxMetadata(DiffTimelineCoreRow row)
    {
        var confidence = row.DiagnosticsMetadata.TryGetValue("confidence", out var c) ? c : "n/a";
        var relation = row.DiagnosticsMetadata.TryGetValue("relationKind", out var r) ? r : "none";
        return new DiffTimelineSemanticUxMetadata(
            SemanticBadge: row.SemanticCategory,
            ConfidenceDisplay: confidence,
            GroupedEditKey: $"{row.Path}|{row.Field}",
            RelationKind: relation);
    }

    public static DiffTimelineRowUxMetadata BuildRowUxMetadata(DiffTimelineCoreRow row)
    {
        return new DiffTimelineRowUxMetadata(
            CompactReady: true,
            IconKey: row.DiffKind.Contains("追加", StringComparison.Ordinal) ? "plus" : row.DiffKind.Contains("削除", StringComparison.Ordinal) ? "minus" : "edit",
            HighlightKey: ResolveSeverity(row),
            NavigationKey: $"{row.TimelineIndex}:{row.Layer}:{row.Frame}",
            StickyGroupReady: true);
    }

    private static string ResolveSeverity(DiffTimelineCoreRow row)
    {
        if (row.DiagnosticsMetadata.TryGetValue("severity", out var severity) && !string.IsNullOrWhiteSpace(severity))
        {
            return severity;
        }

        return row.DiffKind.Contains("削除", StringComparison.Ordinal) || row.DiffKind.Contains("Removed", StringComparison.OrdinalIgnoreCase)
            ? "warning"
            : "info";
    }
}
