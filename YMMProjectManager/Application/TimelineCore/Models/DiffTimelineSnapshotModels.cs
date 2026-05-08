namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelineSnapshotMetadata(
    string SchemaVersion,
    string SourceKind,
    string SourcePath,
    DateTimeOffset CapturedAt,
    IReadOnlyDictionary<string, string> DiagnosticsMetadata);

public sealed record DiffTimelineItemPropertySnapshot(
    string Name,
    string ValueType,
    string? StringValue,
    double? NumericValue,
    bool? BooleanValue,
    TimeSpan? TimeValue,
    IReadOnlyDictionary<string, string> DiagnosticsMetadata);

public sealed record DiffTimelineItemSnapshot(
    string ItemId,
    string DisplayName,
    int TimelineIndex,
    int Layer,
    int Frame,
    int Length,
    IReadOnlyList<DiffTimelineItemPropertySnapshot> Properties,
    IReadOnlyDictionary<string, string> DiagnosticsMetadata);

public sealed record DiffTimelineLayerSnapshot(
    string LayerId,
    string LayerName,
    int LayerOrder,
    IReadOnlyList<DiffTimelineItemSnapshot> Items,
    IReadOnlyDictionary<string, string> DiagnosticsMetadata);

public sealed record DiffTimelineTimelineSnapshot(
    string TimelineId,
    string TimelineName,
    int TimelineOrder,
    IReadOnlyList<DiffTimelineLayerSnapshot> Layers,
    IReadOnlyDictionary<string, string> DiagnosticsMetadata);

public sealed record DiffTimelineProjectSnapshot(
    string ProjectId,
    string ProjectName,
    IReadOnlyList<DiffTimelineTimelineSnapshot> Timelines,
    DiffTimelineSnapshotMetadata Metadata);
