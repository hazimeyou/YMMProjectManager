namespace YMMProjectManager.Application.TimelineCore;

public enum DiffTimelineSemanticChangeKind
{
    Added,
    Removed,
    Changed,
    Moved,
    Renamed,
}

public sealed record DiffTimelineSemanticDiffInput(
    DiffTimelineProjectSnapshot OldSnapshot,
    DiffTimelineProjectSnapshot NewSnapshot,
    IReadOnlyDictionary<string, string> Options);

public sealed record DiffTimelineSemanticChange(
    string ChangeId,
    DiffTimelineSemanticChangeKind Kind,
    string TimelineId,
    string LayerId,
    string ItemId,
    string Field,
    string? OldValue,
    string? NewValue,
    string SemanticCategory,
    double Confidence,
    string Reason,
    IReadOnlyDictionary<string, string> DiagnosticsMetadata);

public sealed record DiffTimelineSemanticDiffResult(
    IReadOnlyList<DiffTimelineSemanticChange> Changes,
    int AddedCount,
    int RemovedCount,
    int ChangedCount,
    int MovedCount,
    int RenamedCount,
    int PropertyChangedCount,
    string SummaryText,
    IReadOnlyDictionary<string, string> StageDiagnostics);
