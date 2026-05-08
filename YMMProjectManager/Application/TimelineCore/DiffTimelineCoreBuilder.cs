using YMMProjectManager.Infrastructure.Diff;

namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineCoreBuilder
{
    public static DiffTimelineCoreSnapshot Build(
        IReadOnlyList<YmmProjectDiffEntry> entries,
        Func<string, string> kindLabel,
        Func<object?, string> fieldLabel,
        Func<object?, string> displayText)
    {
        var items = new List<DiffTimelineCoreItem>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var x = entries[i];
            var id = $"diff-{i}";
            var kind = kindLabel(x.Kind.ToString());
            var field = fieldLabel(x.Field);
            items.Add(new DiffTimelineCoreItem(
                Id: id,
                KindLabel: kind,
                Category: x.Category,
                DisplayName: $"{kind} {field}",
                TimelineIndex: x.TimelineIndex,
                Layer: x.Layer,
                Frame: x.Frame,
                Length: Math.Max(1, x.Length),
                OldValue: displayText(x.Before),
                NewValue: displayText(x.After)));
        }

        return new DiffTimelineCoreSnapshot(items);
    }
}
