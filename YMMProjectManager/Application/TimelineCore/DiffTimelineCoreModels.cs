namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelineCoreItem(
    string Id,
    string KindLabel,
    string Category,
    string DisplayName,
    int TimelineIndex,
    int Layer,
    int Frame,
    int Length,
    string OldValue,
    string NewValue);

public sealed record DiffTimelineCoreGroup(
    string GroupName,
    IReadOnlyList<string> ItemIds,
    int Count);

public sealed record DiffTimelineCoreSnapshot(
    IReadOnlyList<DiffTimelineCoreItem> Items);

public sealed record DiffTimelineCoreResult(
    DiffTimelineCoreSnapshot Snapshot,
    IReadOnlyList<DiffTimelineCoreGroup> Groups);

public sealed record DiffTimelineCoreBuildOptions(
    Func<string, string> KindLabel,
    Func<object?, string> FieldLabel,
    Func<object?, string> DisplayText,
    Func<DiffTimelineCoreItem, bool>? ItemFilter = null,
    Func<DiffTimelineCoreItem, string>? GroupResolver = null);
