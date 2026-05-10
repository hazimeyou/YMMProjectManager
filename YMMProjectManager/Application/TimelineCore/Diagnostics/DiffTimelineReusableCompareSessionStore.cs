namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelinePersistedSnapshotEntry(
    string SnapshotHash,
    string SnapshotName,
    string SourceYmmpPath,
    DateTimeOffset PersistedAt,
    bool SnapshotBodyAvailable,
    IReadOnlyList<string> Tags,
    string Note,
    IReadOnlyDictionary<string, string> ValidationMetadata);

public sealed record DiffTimelineReusableCompareSession(
    string SessionId,
    string OldSnapshotHash,
    string NewSnapshotHash,
    IReadOnlyDictionary<string, string> CompareOptions,
    IReadOnlyDictionary<string, string> FilterState,
    string GroupingMode,
    string CompareSummary,
    string LatestDiagnosticsPath,
    string LatestExportPath,
    string LatestValidationLogPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed class DiffTimelineReusableCompareSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string sessionPath;
    private readonly string snapshotPath;

    public DiffTimelineReusableCompareSessionStore(string diagnosticsDirectory)
    {
        Directory.CreateDirectory(diagnosticsDirectory);
        sessionPath = Path.Combine(diagnosticsDirectory, "difftimeline-reusable-compare-sessions.json");
        snapshotPath = Path.Combine(diagnosticsDirectory, "difftimeline-persisted-snapshots.json");
    }

    public IReadOnlyList<DiffTimelineReusableCompareSession> LoadSessions()
    {
        if (!File.Exists(sessionPath)) return [];
        return JsonSerializer.Deserialize<List<DiffTimelineReusableCompareSession>>(File.ReadAllText(sessionPath), JsonOptions) ?? [];
    }

    public void SaveSession(DiffTimelineReusableCompareSession session)
    {
        var sessions = LoadSessions().ToList();
        sessions.RemoveAll(x => string.Equals(x.SessionId, session.SessionId, StringComparison.Ordinal));
        sessions.Add(session);
        File.WriteAllText(sessionPath, JsonSerializer.Serialize(sessions, JsonOptions));
    }

    public IReadOnlyList<DiffTimelineReusableCompareSession> LatestSessions(int count = 20) =>
        LoadSessions().OrderByDescending(x => x.UpdatedAt).Take(count).ToList();

    public IReadOnlyList<DiffTimelinePersistedSnapshotEntry> LoadPersistedSnapshots()
    {
        if (!File.Exists(snapshotPath)) return [];
        return JsonSerializer.Deserialize<List<DiffTimelinePersistedSnapshotEntry>>(File.ReadAllText(snapshotPath), JsonOptions) ?? [];
    }

    public void SavePersistedSnapshot(DiffTimelinePersistedSnapshotEntry entry)
    {
        var snapshots = LoadPersistedSnapshots().ToList();
        snapshots.RemoveAll(x => string.Equals(x.SnapshotHash, entry.SnapshotHash, StringComparison.Ordinal));
        snapshots.Add(entry);
        File.WriteAllText(snapshotPath, JsonSerializer.Serialize(snapshots, JsonOptions));
    }
}
