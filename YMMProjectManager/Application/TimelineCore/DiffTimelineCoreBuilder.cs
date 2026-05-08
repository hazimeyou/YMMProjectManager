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

    public static DiffTimelineCoreResult BuildResult(
        IReadOnlyList<YmmProjectDiffEntry> entries,
        DiffTimelineCoreBuildOptions options)
    {
        var snapshot = Build(entries, options.KindLabel, options.FieldLabel, options.DisplayText);
        var filtered = options.ItemFilter is null
            ? snapshot.Items
            : snapshot.Items.Where(options.ItemFilter).ToList();
        var groupResolver = options.GroupResolver ?? DefaultGroupResolver;
        var groups = filtered
            .GroupBy(groupResolver)
            .OrderByDescending(x => x.Count())
            .Select(x => new DiffTimelineCoreGroup(
                GroupName: x.Key,
                ItemIds: x.Select(y => y.Id).ToList(),
                Count: x.Count()))
            .ToList();
        return new DiffTimelineCoreResult(
            Snapshot: new DiffTimelineCoreSnapshot(filtered.ToList()),
            Groups: groups);
    }

    private static string DefaultGroupResolver(DiffTimelineCoreItem item)
    {
        if (item.DisplayName.Contains("テキスト", StringComparison.Ordinal))
        {
            return "テキスト変更";
        }

        if (item.DisplayName.Contains("素材パス", StringComparison.Ordinal))
        {
            return "素材パス変更";
        }

        if (item.DisplayName.Contains("フレーム", StringComparison.Ordinal) ||
            item.DisplayName.Contains("レイヤー", StringComparison.Ordinal))
        {
            return "タイムライン移動";
        }

        if (item.DisplayName.Contains("長さ", StringComparison.Ordinal))
        {
            return "長さ変更";
        }

        return "その他";
    }
}
