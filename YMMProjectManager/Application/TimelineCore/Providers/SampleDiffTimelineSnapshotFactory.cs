namespace YMMProjectManager.Application.TimelineCore;

public static class SampleDiffTimelineSnapshotFactory
{
    public static (DiffTimelineProjectSnapshot OldSnapshot, DiffTimelineProjectSnapshot NewSnapshot) CreateForSelfCheck()
    {
        var oldSnapshot = BuildProjectSnapshot("project-1", "Project A", new[]
        {
            BuildTimeline("timeline-1", "Main", 0, new[]
            {
                BuildLayer("layer-1", "Layer A", 0, new[]
                {
                    BuildItem("item-1", "Clip A", 0, 0, 10, 30, new Dictionary<string, string>
                    {
                        ["Text"] = "hello",
                        ["FilePath"] = "a.png",
                    }),
                    BuildItem("item-2", "Clip B", 0, 0, 80, 20, new Dictionary<string, string>
                    {
                        ["Text"] = "to be removed",
                    }),
                }),
            }),
        });

        var newSnapshot = BuildProjectSnapshot("project-1", "Project A", new[]
        {
            BuildTimeline("timeline-1", "Main", 0, new[]
            {
                BuildLayer("layer-1", "Layer A", 0, new[]
                {
                    BuildItem("item-1", "Clip A Renamed", 0, 1, 20, 30, new Dictionary<string, string>
                    {
                        ["Text"] = "hello world",
                        ["FilePath"] = "a.png",
                    }),
                    BuildItem("item-3", "Clip C Added", 0, 0, 120, 24, new Dictionary<string, string>
                    {
                        ["Text"] = "added",
                    }),
                }),
            }),
        });

        return (oldSnapshot, newSnapshot);
    }

    private static DiffTimelineProjectSnapshot BuildProjectSnapshot(string projectId, string projectName, IReadOnlyList<DiffTimelineTimelineSnapshot> timelines)
    {
        return new DiffTimelineProjectSnapshot(
            projectId,
            projectName,
            timelines,
            new DiffTimelineSnapshotMetadata(
                SchemaVersion: "1.0",
                SourceKind: "sample",
                SourcePath: projectId,
                CapturedAt: DateTimeOffset.UtcNow,
                DiagnosticsMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["factory"] = nameof(SampleDiffTimelineSnapshotFactory),
                }));
    }

    private static DiffTimelineTimelineSnapshot BuildTimeline(string id, string name, int order, IReadOnlyList<DiffTimelineLayerSnapshot> layers)
        => new(id, name, order, layers, new Dictionary<string, string>());

    private static DiffTimelineLayerSnapshot BuildLayer(string id, string name, int order, IReadOnlyList<DiffTimelineItemSnapshot> items)
        => new(id, name, order, items, new Dictionary<string, string>());

    private static DiffTimelineItemSnapshot BuildItem(string id, string name, int timelineIndex, int layer, int frame, int length, IReadOnlyDictionary<string, string> properties)
    {
        var propertyList = properties.Select(kv => new DiffTimelineItemPropertySnapshot(
            Name: kv.Key,
            ValueType: "string",
            StringValue: kv.Value,
            NumericValue: null,
            BooleanValue: null,
            TimeValue: null,
            DiagnosticsMetadata: new Dictionary<string, string>())).ToList();

        return new DiffTimelineItemSnapshot(
            ItemId: id,
            DisplayName: name,
            TimelineIndex: timelineIndex,
            Layer: layer,
            Frame: frame,
            Length: length,
            Properties: propertyList,
            DiagnosticsMetadata: new Dictionary<string, string>());
    }
}
