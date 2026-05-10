namespace YMMProjectManager.Application.TimelineCore;

public sealed class DiffTimelineSnapshotRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string storagePath;

    public DiffTimelineSnapshotRepository(string storageDirectory)
    {
        Directory.CreateDirectory(storageDirectory);
        storagePath = Path.Combine(storageDirectory, "difftimeline-snapshot-repository.json");
    }

    public IReadOnlyList<DiffTimelineSnapshotRepositoryEntry> Load()
    {
        if (!File.Exists(storagePath))
        {
            return [];
        }

        var json = File.ReadAllText(storagePath);
        return JsonSerializer.Deserialize<List<DiffTimelineSnapshotRepositoryEntry>>(json, JsonOptions) ?? [];
    }

    public void SaveSnapshot(DiffTimelineSnapshotRepositoryEntry entry)
    {
        var all = Load().ToList();
        all.RemoveAll(x => string.Equals(x.Snapshot.Metadata.SnapshotHash, entry.Snapshot.Metadata.SnapshotHash, StringComparison.Ordinal));
        all.Add(entry);
        File.WriteAllText(storagePath, JsonSerializer.Serialize(all, JsonOptions));
    }

    public bool TryGetSnapshotByHash(string snapshotHash, out DiffTimelineProjectSnapshot? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(snapshotHash))
        {
            return false;
        }

        var entry = Load().FirstOrDefault(x => string.Equals(x.Snapshot.Metadata.SnapshotHash, snapshotHash, StringComparison.Ordinal));
        if (entry is null)
        {
            return false;
        }

        snapshot = entry.Snapshot;
        return true;
    }

    public DiffTimelineSnapshotRetentionPlan BuildRetentionPlan(int keepLatestCount)
    {
        var all = Load()
            .OrderByDescending(x => x.CreatedAt)
            .ToList();
        var candidates = all
            .Skip(Math.Max(0, keepLatestCount))
            .Select(x => x.Snapshot.Metadata.SnapshotHash)
            .ToList();

        return new DiffTimelineSnapshotRetentionPlan(
            KeepLatestCount: keepLatestCount,
            CleanupCandidateHashes: candidates,
            Recommendation: candidates.Count == 0 ? "no-cleanup-needed" : "cleanup-candidates-identified");
    }

    public DiffTimelineSnapshotBrowserState BuildBrowserState(string latestValidationState)
    {
        var all = Load()
            .OrderByDescending(x => x.CreatedAt)
            .ToList();
        var items = all.Select(x => new DiffTimelineSnapshotListItem(
            SnapshotHash: x.Snapshot.Metadata.SnapshotHash,
            SnapshotName: x.Name,
            CreatedAt: x.CreatedAt,
            SourceProject: x.SourceProject,
            Note: x.Note,
            Tags: x.Tags,
            TimelineCount: x.Snapshot.Timelines.Count,
            ItemCount: x.Snapshot.Timelines.Sum(t => t.Layers.Sum(l => l.Items.Count)))).ToList();
        var candidates = items
            .Zip(items.Skip(1), (n, o) => new DiffTimelineComparisonCandidate(
                OldSnapshotHash: o.SnapshotHash,
                NewSnapshotHash: n.SnapshotHash,
                Label: $"{o.SnapshotName} -> {n.SnapshotName}",
                CreatedAt: n.CreatedAt))
            .Take(20)
            .ToList();
        var selected = items.FirstOrDefault();
        var detail = selected is null
            ? null
            : new DiffTimelineSnapshotDetailSummary(
                SnapshotHash: selected.SnapshotHash,
                TimelineCount: selected.TimelineCount,
                LayerCount: all.First(x => x.Snapshot.Metadata.SnapshotHash == selected.SnapshotHash).Snapshot.Timelines.Sum(t => t.Layers.Count),
                ItemCount: selected.ItemCount,
                Metadata: all.First(x => x.Snapshot.Metadata.SnapshotHash == selected.SnapshotHash).Snapshot.Metadata.DiagnosticsMetadata);

        return new DiffTimelineSnapshotBrowserState(items, candidates, detail, latestValidationState);
    }
}
