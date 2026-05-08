namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelineCoreRow(
    string RowId,
    string SourceItemId,
    string Title,
    string Subtitle,
    string Detail,
    string DisplayLabel,
    string OldValue,
    string NewValue,
    string GroupKey,
    string GroupDisplayLabel,
    string FilterKey,
    string SemanticCategory,
    string DiffKind,
    string Path,
    string Field,
    int TimelineIndex,
    int Layer,
    int Frame,
    int Length,
    long SortKey,
    int Order,
    IReadOnlyDictionary<string, string> DiagnosticsMetadata);

public sealed record DiffTimelineCoreRowSet(
    IReadOnlyList<DiffTimelineCoreRow> Rows,
    IReadOnlyDictionary<string, int> GroupCounts,
    IReadOnlyDictionary<string, int> SemanticCategoryCounts);
