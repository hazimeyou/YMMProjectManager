namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelineSnapshotRequest(
    string SourcePath,
    string SourceKind,
    IReadOnlyDictionary<string, string> Options);

public interface IDiffTimelineSnapshotProvider
{
    Task<DiffTimelineProjectSnapshot> CaptureAsync(DiffTimelineSnapshotRequest request, CancellationToken cancellationToken = default);
}

public interface IDiffTimelineSnapshotSerializer
{
    string Serialize(DiffTimelineProjectSnapshot snapshot);
    DiffTimelineProjectSnapshot Deserialize(string content);
}
