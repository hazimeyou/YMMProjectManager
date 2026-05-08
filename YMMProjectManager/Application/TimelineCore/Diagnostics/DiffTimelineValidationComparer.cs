namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineValidationComparer
{
    public static DiffTimelineValidationComparerResult Compare(
        DiffTimelineExistingRouteSummary existing,
        DiffTimelineStandalonePipelineResult standalone)
    {
        var standaloneKeys = standalone.CoreResult.RowSet.Rows.Select(BuildKey).ToHashSet(StringComparer.Ordinal);
        var existingKeys = existing.Keys.ToHashSet(StringComparer.Ordinal);

        var missing = existingKeys.Where(k => !standaloneKeys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var extra = standaloneKeys.Where(k => !existingKeys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var common = existingKeys.Count - missing.Count;
        var denom = Math.Max(1, existingKeys.Count);
        var keyMatchRate = (double)common / denom;

        var reasons = new List<string>();
        if (existing.ItemCount != standalone.CoreResult.RowSet.Rows.Count) reasons.Add("item-count-mismatch");
        if (existing.GroupCount != standalone.CoreResult.Groups.Count) reasons.Add("group-count-mismatch");
        if (existing.AddedCount != standalone.Diagnostics.AddedCount) reasons.Add("added-count-mismatch");
        if (existing.RemovedCount != standalone.Diagnostics.RemovedCount) reasons.Add("removed-count-mismatch");
        if (existing.ChangedCount != standalone.Diagnostics.ChangedCount) reasons.Add("changed-count-mismatch");
        if (missing.Count > 0) reasons.Add("missing-rows");
        if (extra.Count > 0) reasons.Add("extra-rows");

        return new DiffTimelineValidationComparerResult(
            existing.ItemCount,
            standalone.CoreResult.RowSet.Rows.Count,
            existing.GroupCount,
            standalone.CoreResult.Groups.Count,
            existing.AddedCount,
            standalone.Diagnostics.AddedCount,
            existing.RemovedCount,
            standalone.Diagnostics.RemovedCount,
            existing.ChangedCount,
            standalone.Diagnostics.ChangedCount,
            common,
            missing.Count,
            extra.Count,
            keyMatchRate,
            missing,
            extra,
            reasons);
    }

    private static string BuildKey(DiffTimelineCoreRow row)
    {
        return $"{row.DiffKind}|{row.Path}|{row.Field}|{row.Frame}|{row.Layer}|{row.Length}";
    }
}
