namespace YMMProjectManager.Application.TimelineCore;

public sealed class InMemoryDiffTimelineSnapshotProvider : IDiffTimelineSnapshotProvider
{
    private readonly IReadOnlyDictionary<string, DiffTimelineProjectSnapshot> snapshots;

    public InMemoryDiffTimelineSnapshotProvider(IReadOnlyDictionary<string, DiffTimelineProjectSnapshot> snapshots)
    {
        this.snapshots = snapshots;
    }

    public Task<DiffTimelineProjectSnapshot> CaptureAsync(DiffTimelineSnapshotRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshots.TryGetValue(request.SourcePath, out var snapshot))
        {
            return Task.FromResult(snapshot);
        }

        throw new KeyNotFoundException($"Snapshot not found for source path: {request.SourcePath}");
    }
}
