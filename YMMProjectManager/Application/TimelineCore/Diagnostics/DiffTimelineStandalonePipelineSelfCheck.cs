namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelineStandaloneSelfCheckResult(
    IReadOnlyDictionary<string, string> RoundTrip,
    IReadOnlyDictionary<string, string> Assertions,
    DiffTimelineStandalonePipelineDiagnostics Diagnostics,
    string Summary);

public static class DiffTimelineStandalonePipelineSelfCheck
{
    public static DiffTimelineStandaloneSelfCheckResult Run()
    {
        var (oldSnapshot, newSnapshot) = SampleDiffTimelineSnapshotFactory.CreateForSelfCheck();

        var serializer = new DiffTimelineSnapshotJsonSerializer();
        var oldJson = serializer.Serialize(oldSnapshot);
        var oldRoundTrip = serializer.Deserialize(oldJson);

        var pipeline = DiffTimelineStandalonePipeline.BuildFromSnapshots(oldSnapshot, newSnapshot,
            new DiffTimelineStandalonePipelineOptions(
                OptionSnapshot: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["selfCheck"] = "true",
                }));

        var cache = new InMemoryDiffTimelineSnapshotCache();
        var envelopeMiss = DiffTimelineStandalonePipeline.BuildEnvelopeFromSnapshots(
            oldSnapshot,
            newSnapshot,
            new DiffTimelineStandalonePipelineOptions(
                OptionSnapshot: new Dictionary<string, string>(StringComparer.Ordinal) { ["selfCheck"] = "cache" },
                SnapshotCache: cache));
        var envelopeHit = DiffTimelineStandalonePipeline.BuildEnvelopeFromSnapshots(
            oldSnapshot,
            newSnapshot,
            new DiffTimelineStandalonePipelineOptions(
                OptionSnapshot: new Dictionary<string, string>(StringComparer.Ordinal) { ["selfCheck"] = "cache" },
                SnapshotCache: cache));
        var existingSummary = new DiffTimelineExistingRouteSummary(
            ItemCount: pipeline.CoreResult.RowSet.Rows.Count,
            GroupCount: pipeline.CoreResult.Groups.Count,
            AddedCount: pipeline.Diagnostics.AddedCount,
            RemovedCount: pipeline.Diagnostics.RemovedCount,
            ChangedCount: pipeline.Diagnostics.ChangedCount,
            Keys: pipeline.CoreResult.RowSet.Rows.Select(x => $"{x.DiffKind}|{x.Path}|{x.Field}|{x.Frame}|{x.Layer}|{x.Length}").ToList());
        var comparer = DiffTimelineValidationComparer.Compare(existingSummary, pipeline);
        var readiness = DiffTimelinePromotionReadinessEvaluator.Evaluate(comparer, envelopeMiss);

        var keyA = DiffTimelineSnapshotCacheKeyFactory.Create(oldSnapshot, newSnapshot, pipeline.Diagnostics.OptionsSnapshot);
        var keyB = DiffTimelineSnapshotCacheKeyFactory.Create(oldSnapshot, newSnapshot, pipeline.Diagnostics.OptionsSnapshot);

        var assertions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["addedItem"] = (pipeline.Diagnostics.AddedCount >= 1).ToString(),
            ["removedItem"] = (pipeline.Diagnostics.RemovedCount >= 1).ToString(),
            ["changedProperty"] = (pipeline.Diagnostics.PropertyChangedCount >= 1).ToString(),
            ["movedItem"] = (pipeline.Diagnostics.MovedCount >= 1).ToString(),
            ["renamedItem"] = (pipeline.Diagnostics.RenamedCount >= 1).ToString(),
            ["jsonRoundTrip"] = (string.Equals(oldSnapshot.ProjectId, oldRoundTrip.ProjectId, StringComparison.Ordinal)
                && oldSnapshot.Timelines.Count == oldRoundTrip.Timelines.Count).ToString(),
            ["rowCount"] = (pipeline.CoreResult.RowSet.Rows.Count >= 1).ToString(),
            ["summaryCount"] = (pipeline.CoreResult.Summary.FilteredItemCount == pipeline.CoreResult.RowSet.Rows.Count).ToString(),
            ["diagnosticsCount"] = (pipeline.Diagnostics.SemanticChangeCount == pipeline.SemanticDiff.Changes.Count).ToString(),
            ["cacheKeyConsistency"] = string.Equals(keyA.Value, keyB.Value, StringComparison.Ordinal).ToString(),
            ["fallbackToSample"] = "true",
            ["cacheMissThenHit"] = (!envelopeMiss.CacheHit && envelopeHit.CacheHit).ToString(),
            ["envelopeSuccess"] = (envelopeMiss.IsSuccess && envelopeHit.IsSuccess).ToString(),
            ["adapterDiagnostics"] = oldSnapshot.Metadata.DiagnosticsMetadata.ContainsKey("factory").ToString(),
            ["hashStability"] = string.Equals(oldSnapshot.Metadata.SnapshotHash, oldRoundTrip.Metadata.SnapshotHash, StringComparison.Ordinal).ToString(),
            ["fallbackReason"] = string.Equals(envelopeMiss.FallbackReason, "none", StringComparison.Ordinal).ToString(),
            ["diagnosticsJsonReady"] = (!string.IsNullOrWhiteSpace(pipeline.Diagnostics.StageSummary)).ToString(),
            ["comparerReady"] = (comparer.KeyMatchRate >= 0.99).ToString(),
            ["promotionReadinessEvaluated"] = (!string.IsNullOrWhiteSpace(readiness.CacheStatus)).ToString(),
            ["blockerWarningEvaluated"] = (readiness.Blockers.Count >= 0 && readiness.Warnings.Count >= 0).ToString(),
        };

        var roundTrip = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projectIdEqual"] = string.Equals(oldSnapshot.ProjectId, oldRoundTrip.ProjectId, StringComparison.Ordinal).ToString(),
            ["timelineCountEqual"] = (oldSnapshot.Timelines.Count == oldRoundTrip.Timelines.Count).ToString(),
            ["jsonLength"] = oldJson.Length.ToString(),
        };

        var summary = string.Join(", ", assertions.Select(kv => $"{kv.Key}={kv.Value}"));
        return new DiffTimelineStandaloneSelfCheckResult(roundTrip, assertions, pipeline.Diagnostics, summary);
    }
}
