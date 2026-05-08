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
        var envelopeMiss = DiffTimelineStandalonePipeline.BuildEnvelopeFromSnapshots(oldSnapshot, newSnapshot,
            new DiffTimelineStandalonePipelineOptions(OptionSnapshot: new Dictionary<string, string> { ["selfCheck"] = "cache" }, SnapshotCache: cache));
        var envelopeHit = DiffTimelineStandalonePipeline.BuildEnvelopeFromSnapshots(oldSnapshot, newSnapshot,
            new DiffTimelineStandalonePipelineOptions(OptionSnapshot: new Dictionary<string, string> { ["selfCheck"] = "cache" }, SnapshotCache: cache));

        var existingSummary = new DiffTimelineExistingRouteSummary(
            ItemCount: pipeline.CoreResult.RowSet.Rows.Count,
            GroupCount: pipeline.CoreResult.Groups.Count,
            AddedCount: pipeline.Diagnostics.AddedCount,
            RemovedCount: pipeline.Diagnostics.RemovedCount,
            ChangedCount: pipeline.Diagnostics.ChangedCount,
            Keys: pipeline.CoreResult.RowSet.Rows.Select(x => $"{x.DiffKind}|{x.Path}|{x.Field}|{x.Frame}|{x.Layer}|{x.Length}").ToList());

        var comparer = DiffTimelineValidationComparer.Compare(existingSummary, pipeline);
        var readiness = DiffTimelinePromotionReadinessEvaluator.Evaluate(comparer, envelopeMiss);
        var gate = DiffTimelineStandalonePromotionGate.Evaluate(readiness);

        var run1 = new DiffTimelineValidationRunRecord(DateTimeOffset.Now.AddMinutes(-1), "p", oldSnapshot.Metadata.SnapshotHash, newSnapshot.Metadata.SnapshotHash, "standalone", "standalone", true, "promotion-allowed", 0.99, [], [], false, "d1", "ok", "none");
        var run2 = run1 with { Timestamp = DateTimeOffset.Now, ComparerConfidence = 0.70, Blockers = ["confidence-below-threshold"], DiagnosticsPath = "" };
        var regression = DiffTimelineValidationRegressionDetector.Detect(run2, run1);
        var trend = DiffTimelineValidationRegressionDetector.EvaluateTrend(new DiffTimelineValidationRunHistory([run1, run2]));

        var tempDir = Path.Combine(Path.GetTempPath(), "difftimeline-selfcheck");
        var historyPath = DiffTimelineValidationRunHistoryWriter.Append(tempDir, run1, keepLast: 10);
        var loaded = DiffTimelineValidationRunHistoryWriter.Load(historyPath);

        var assertions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["runHistoryAppend"] = File.Exists(historyPath).ToString(),
            ["runHistoryLoad"] = (loaded.Runs.Count >= 1).ToString(),
            ["regressionDetection"] = regression.HasRegression.ToString(),
            ["consecutiveSuccess"] = (trend.ConsecutiveSuccessCount >= 0).ToString(),
            ["blockerIncrease"] = regression.Blockers.Contains("blockers-increased").ToString(),
            ["fallbackIncrease"] = regression.Warnings.Contains("fallback-increased").ToString(),
            ["cacheHitMiss"] = (!envelopeMiss.CacheHit && envelopeHit.CacheHit).ToString(),
            ["gateEvaluated"] = (!string.IsNullOrWhiteSpace(gate.Reason)).ToString(),
            ["jsonRoundTrip"] = (string.Equals(oldSnapshot.ProjectId, oldRoundTrip.ProjectId, StringComparison.Ordinal)).ToString(),
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
