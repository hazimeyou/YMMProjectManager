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
