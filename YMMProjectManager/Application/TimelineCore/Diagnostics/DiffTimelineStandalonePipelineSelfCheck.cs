namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineStandalonePipelineSelfCheck
{
    public static IReadOnlyDictionary<string, string> RunRoundTripCheck(DiffTimelineProjectSnapshot snapshot)
    {
        var serializer = new DiffTimelineSnapshotJsonSerializer();
        var json = serializer.Serialize(snapshot);
        var restored = serializer.Deserialize(json);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projectIdEqual"] = string.Equals(snapshot.ProjectId, restored.ProjectId, StringComparison.Ordinal).ToString(),
            ["timelineCountEqual"] = (snapshot.Timelines.Count == restored.Timelines.Count).ToString(),
            ["jsonLength"] = json.Length.ToString(),
        };
    }
}
