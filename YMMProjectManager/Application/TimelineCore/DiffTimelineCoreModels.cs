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

public sealed record DiffTimelineCoreSnapshot(
    IReadOnlyList<DiffTimelineCoreItem> Items);

