namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelineCoreItem(
    string Id,
    string KindLabel,
    string FieldLabel,
    string Category,
    string SemanticCategory,
    string ScopeLabel,
    string PathLabel,
    string DisplayLabel,
    string GroupKey,
    string FilterKey,
    int TimelineIndex,
    int Layer,
    int Frame,
    int Length,
    string OldValue,
    string NewValue,
    IReadOnlyDictionary<string, string> DiagnosticsMetadata);

public sealed record DiffTimelineCoreGroup(
    string GroupKey,
    string GroupDisplayLabel,
    IReadOnlyList<string> ItemIds,
    int Count);

public sealed record DiffTimelineCoreSnapshot(
    IReadOnlyList<DiffTimelineCoreItem> Items);

public sealed record DiffTimelineCoreBuildOptions(
    Func<string, string>? KindLabelResolver = null,
    Func<object?, string>? FieldLabelResolver = null,
    Func<string, string>? PathLabelResolver = null,
    Func<object?, string>? ValueDisplayResolver = null,
    Func<DiffTimelineCoreItem, bool>? ItemFilter = null,
    Func<DiffTimelineCoreItem, string>? GroupResolver = null,
    Func<DiffTimelineCoreItem, string>? GroupDisplayLabelResolver = null,
    IReadOnlyDictionary<string, string>? OptionSnapshot = null);

public sealed record DiffTimelineCoreSummary(
    int TotalItemCount,
    int FilteredItemCount,
    int GroupCount,
    int AddedCount,
    int RemovedCount,
    int ChangedCount,
    int MovedCount,
    IReadOnlyDictionary<string, int> SemanticCategoryCounts,
    IReadOnlyDictionary<string, string> BuildOptionsSnapshot,
    string SummaryText);

public sealed record DiffTimelineCoreResult(
    DiffTimelineCoreSnapshot Snapshot,
    IReadOnlyList<DiffTimelineCoreGroup> Groups,
    DiffTimelineCoreSummary Summary,
    DiffTimelineCoreRowSet RowSet);
