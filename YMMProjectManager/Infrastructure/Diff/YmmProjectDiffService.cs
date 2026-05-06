using YMMProjectManager.Infrastructure.Ymm;

namespace YMMProjectManager.Infrastructure.Diff;

public sealed class YmmProjectDiffService
{
    private static readonly string[] Fields = ["Text", "FilePath", "Frame", "Layer", "Length"];
    private readonly YmmProjectParser parser = new();

    public IReadOnlyList<YmmProjectDiffEntry> Diff(string beforeNormalizedJson, string afterNormalizedJson)
    {
        var before = parser.Parse(beforeNormalizedJson);
        var after = parser.Parse(afterNormalizedJson);
        var diffs = new List<YmmProjectDiffEntry>();

        var pairedBefore = new HashSet<YmmItemModel>();
        var pairedAfter = new HashSet<YmmItemModel>();

        PairByInternalId(before.Items, after.Items, pairedBefore, pairedAfter, diffs);
        PairByFallbackKey(before.Items, after.Items, pairedBefore, pairedAfter, diffs);

        foreach (var b in before.Items.Where(x => !pairedBefore.Contains(x)))
        {
            EmitAddRemove(diffs, YmmProjectDiffKind.Removed, b, null);
        }

        foreach (var a in after.Items.Where(x => !pairedAfter.Contains(x)))
        {
            EmitAddRemove(diffs, YmmProjectDiffKind.Added, null, a);
        }

        return diffs;
    }

    private static void PairByInternalId(
        IReadOnlyList<YmmItemModel> beforeItems,
        IReadOnlyList<YmmItemModel> afterItems,
        ISet<YmmItemModel> pairedBefore,
        ISet<YmmItemModel> pairedAfter,
        IList<YmmProjectDiffEntry> diffs)
    {
        var afterById = afterItems
            .Where(x => !string.IsNullOrWhiteSpace(x.InternalId))
            .GroupBy(x => x.InternalId!, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => new Queue<YmmItemModel>(x), StringComparer.Ordinal);

        foreach (var b in beforeItems.Where(x => !string.IsNullOrWhiteSpace(x.InternalId)))
        {
            if (!afterById.TryGetValue(b.InternalId!, out var queue) || queue.Count == 0)
            {
                continue;
            }

            var a = queue.Dequeue();
            pairedBefore.Add(b);
            pairedAfter.Add(a);
            EmitChanges(diffs, b, a);
        }
    }

    private static void PairByFallbackKey(
        IReadOnlyList<YmmItemModel> beforeItems,
        IReadOnlyList<YmmItemModel> afterItems,
        ISet<YmmItemModel> pairedBefore,
        ISet<YmmItemModel> pairedAfter,
        IList<YmmProjectDiffEntry> diffs)
    {
        var remainingBefore = beforeItems.Where(x => !pairedBefore.Contains(x)).ToList();
        var remainingAfter = afterItems.Where(x => !pairedAfter.Contains(x)).ToList();

        var afterByKey = remainingAfter
            .GroupBy(YmmItemMatcher.BuildKey, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => new Queue<YmmItemModel>(x), StringComparer.Ordinal);

        foreach (var b in remainingBefore)
        {
            var key = YmmItemMatcher.BuildKey(b);
            if (!afterByKey.TryGetValue(key, out var queue) || queue.Count == 0)
            {
                continue;
            }

            var a = queue.Dequeue();
            pairedBefore.Add(b);
            pairedAfter.Add(a);
            EmitChanges(diffs, b, a);
        }
    }

    private static void EmitChanges(IList<YmmProjectDiffEntry> diffs, YmmItemModel before, YmmItemModel after)
    {
        var moved = before.Frame != after.Frame || before.Layer != after.Layer || before.TimelineIndex != after.TimelineIndex;
        if (moved)
        {
            diffs.Add(new YmmProjectDiffEntry
            {
                Kind = YmmProjectDiffKind.Moved,
                Field = "Position",
                Scope = after.Scope,
                Before = $"T={before.TimelineIndex},L={before.Layer},F={before.Frame}",
                After = $"T={after.TimelineIndex},L={after.Layer},F={after.Frame}",
            });
        }

        foreach (var field in Fields)
        {
            var beforeValue = before.Fields.GetValueOrDefault(field);
            var afterValue = after.Fields.GetValueOrDefault(field);
            if (!string.Equals(beforeValue, afterValue, StringComparison.Ordinal))
            {
                diffs.Add(new YmmProjectDiffEntry
                {
                    Kind = YmmProjectDiffKind.Changed,
                    Field = field,
                    Scope = after.Scope,
                    Before = beforeValue,
                    After = afterValue,
                });
            }
        }
    }

    private static void EmitAddRemove(IList<YmmProjectDiffEntry> diffs, YmmProjectDiffKind kind, YmmItemModel? before, YmmItemModel? after)
    {
        var item = before ?? after;
        if (item is null)
        {
            return;
        }

        foreach (var field in Fields)
        {
            if (!item.Fields.TryGetValue(field, out var value))
            {
                continue;
            }

            diffs.Add(new YmmProjectDiffEntry
            {
                Kind = kind,
                Field = field,
                Scope = item.Scope,
                Before = kind == YmmProjectDiffKind.Removed ? value : null,
                After = kind == YmmProjectDiffKind.Added ? value : null,
            });
        }
    }
}
