namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineSnapshotDiffBuilder
{
    public static DiffTimelineSemanticDiffResult BuildSemanticDiff(DiffTimelineSemanticDiffInput input)
    {
        var oldIndex = BuildIndex(Flatten(input.OldSnapshot));
        var newIndex = BuildIndex(Flatten(input.NewSnapshot));

        var changes = new List<DiffTimelineSemanticChange>();
        var added = 0;
        var removed = 0;
        var changed = 0;
        var moved = 0;
        var renamed = 0;
        var propertyChanged = 0;

        foreach (var (key, oldNode) in oldIndex)
        {
            if (!newIndex.TryGetValue(key, out var newNode))
            {
                removed++;
                changes.Add(CreateChange(DiffTimelineSemanticChangeKind.Removed, oldNode, null, "Item", oldNode.Item.DisplayName, null, "Item missing in new snapshot", "Removed", 1.0));
                continue;
            }

            if (!string.Equals(oldNode.Item.DisplayName, newNode.Item.DisplayName, StringComparison.Ordinal))
            {
                renamed++;
                changed++;
                changes.Add(CreateChange(DiffTimelineSemanticChangeKind.Renamed, oldNode, newNode, "DisplayName", oldNode.Item.DisplayName, newNode.Item.DisplayName, "Display name changed", "Text", 0.9));
            }

            if (oldNode.Item.Frame != newNode.Item.Frame || oldNode.Item.Layer != newNode.Item.Layer || oldNode.Item.TimelineIndex != newNode.Item.TimelineIndex)
            {
                moved++;
                changed++;
                changes.Add(CreateChange(
                    DiffTimelineSemanticChangeKind.Moved,
                    oldNode,
                    newNode,
                    "Position",
                    $"T={oldNode.Item.TimelineIndex},L={oldNode.Item.Layer},F={oldNode.Item.Frame}",
                    $"T={newNode.Item.TimelineIndex},L={newNode.Item.Layer},F={newNode.Item.Frame}",
                    "Frame/layer/timeline moved",
                    "TimelinePosition",
                    1.0));
            }

            var oldProps = oldNode.Item.Properties.ToDictionary(p => p.Name, StringComparer.Ordinal);
            var newProps = newNode.Item.Properties.ToDictionary(p => p.Name, StringComparer.Ordinal);
            foreach (var propertyName in oldProps.Keys.Union(newProps.Keys, StringComparer.Ordinal))
            {
                oldProps.TryGetValue(propertyName, out var oldProp);
                newProps.TryGetValue(propertyName, out var newProp);
                var oldValue = ToComparableValue(oldProp);
                var newValue = ToComparableValue(newProp);
                if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
                {
                    propertyChanged++;
                    changed++;
                    var semantic = propertyName.Contains("Path", StringComparison.OrdinalIgnoreCase)
                        ? "MediaPath"
                        : propertyName.Contains("Frame", StringComparison.OrdinalIgnoreCase)
                            ? "TimelinePosition"
                            : "Property";
                    changes.Add(CreateChange(DiffTimelineSemanticChangeKind.Changed, oldNode, newNode, propertyName, oldValue, newValue, "Property value changed", semantic, 0.8));
                }
            }
        }

        foreach (var (key, newNode) in newIndex)
        {
            if (oldIndex.ContainsKey(key))
            {
                continue;
            }

            added++;
            changes.Add(CreateChange(DiffTimelineSemanticChangeKind.Added, null, newNode, "Item", null, newNode.Item.DisplayName, "Item appears in new snapshot", "Added", 1.0));
        }

        var summary = $"semantic={changes.Count}, +{added}/-{removed}/~{changed}/>{moved}/renamed={renamed}/prop={propertyChanged}";
        return new DiffTimelineSemanticDiffResult(
            changes,
            added,
            removed,
            changed,
            moved,
            renamed,
            propertyChanged,
            summary,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["oldIndexedItems"] = oldIndex.Count.ToString(),
                ["newIndexedItems"] = newIndex.Count.ToString(),
            });
    }

    public static IReadOnlyList<YmmProjectDiffEntry> BuildCoreDiffEntries(DiffTimelineSemanticDiffResult semantic)
    {
        var entries = new List<YmmProjectDiffEntry>(semantic.Changes.Count);
        foreach (var change in semantic.Changes)
        {
            var kind = change.Kind switch
            {
                DiffTimelineSemanticChangeKind.Added => YmmProjectDiffKind.Added,
                DiffTimelineSemanticChangeKind.Removed => YmmProjectDiffKind.Removed,
                DiffTimelineSemanticChangeKind.Moved => YmmProjectDiffKind.Moved,
                _ => YmmProjectDiffKind.Changed,
            };

            entries.Add(new YmmProjectDiffEntry
            {
                Kind = kind,
                Field = change.Field,
                Scope = change.TimelineId,
                Category = change.LayerId,
                TimelineIndex = ExtractInt(change.DiagnosticsMetadata, "timelineIndex"),
                Layer = ExtractInt(change.DiagnosticsMetadata, "layer"),
                Frame = ExtractInt(change.DiagnosticsMetadata, "frame"),
                Length = Math.Max(1, ExtractInt(change.DiagnosticsMetadata, "length", 1)),
                Before = change.OldValue,
                After = change.NewValue,
            });
        }

        return entries;
    }

    private static IEnumerable<SnapshotNode> Flatten(DiffTimelineProjectSnapshot snapshot)
    {
        foreach (var timeline in snapshot.Timelines)
        {
            foreach (var layer in timeline.Layers)
            {
                foreach (var item in layer.Items)
                {
                    yield return new SnapshotNode($"{timeline.TimelineId}|{layer.LayerId}|{item.ItemId}", timeline.TimelineId, layer.LayerId, item);
                }
            }
        }
    }

    private static Dictionary<string, SnapshotNode> BuildIndex(IEnumerable<SnapshotNode> nodes)
    {
        var index = new Dictionary<string, SnapshotNode>(StringComparer.Ordinal);
        var duplicateCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            if (index.TryAdd(node.Key, node))
            {
                continue;
            }

            duplicateCounts.TryGetValue(node.Key, out var count);
            count++;
            duplicateCounts[node.Key] = count;
            var disambiguatedKey = $"{node.Key}#dup{count}";
            index[disambiguatedKey] = node with { Key = disambiguatedKey };
        }

        return index;
    }

    private static DiffTimelineSemanticChange CreateChange(
        DiffTimelineSemanticChangeKind kind,
        SnapshotNode? oldNode,
        SnapshotNode? newNode,
        string field,
        string? oldValue,
        string? newValue,
        string reason,
        string semanticCategory,
        double confidence)
    {
        var baseNode = newNode ?? oldNode!;
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["timelineIndex"] = baseNode.Item.TimelineIndex.ToString(),
            ["layer"] = baseNode.Item.Layer.ToString(),
            ["frame"] = baseNode.Item.Frame.ToString(),
            ["length"] = baseNode.Item.Length.ToString(),
            ["reason"] = reason,
        };

        return new DiffTimelineSemanticChange(
            ChangeId: Guid.NewGuid().ToString("N"),
            Kind: kind,
            TimelineId: baseNode.TimelineId,
            LayerId: baseNode.LayerId,
            ItemId: baseNode.Item.ItemId,
            Field: field,
            OldValue: oldValue,
            NewValue: newValue,
            SemanticCategory: semanticCategory,
            Confidence: confidence,
            Reason: reason,
            DiagnosticsMetadata: metadata);
    }

    private static string? ToComparableValue(DiffTimelineItemPropertySnapshot? property)
    {
        if (property is null)
        {
            return null;
        }

        return property.StringValue
            ?? property.NumericValue?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? property.BooleanValue?.ToString()
            ?? property.TimeValue?.ToString()
            ?? string.Empty;
    }

    private static int ExtractInt(IReadOnlyDictionary<string, string> source, string key, int defaultValue = 0)
    {
        return source.TryGetValue(key, out var value) && int.TryParse(value, out var number)
            ? number
            : defaultValue;
    }

    private sealed record SnapshotNode(string Key, string TimelineId, string LayerId, DiffTimelineItemSnapshot Item);
}
