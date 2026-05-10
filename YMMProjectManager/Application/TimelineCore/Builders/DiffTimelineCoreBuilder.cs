namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineCoreBuilder
{
    public static DiffTimelineCoreResult BuildResult(
        IReadOnlyList<YmmProjectDiffEntry> entries,
        DiffTimelineCoreBuildOptions? options = null)
    {
        options ??= new DiffTimelineCoreBuildOptions();

        var kindLabelResolver = options.KindLabelResolver ?? DiffTimelineDisplayLabelResolver.ToDiffKindLabel;
        var fieldLabelResolver = options.FieldLabelResolver ?? DiffTimelineDisplayLabelResolver.ToFieldLabel;
        var pathLabelResolver = options.PathLabelResolver ?? DiffTimelineDisplayLabelResolver.ToPathLabel;
        var valueDisplayResolver = options.ValueDisplayResolver ?? (x => x?.ToString() ?? string.Empty);

        var allItems = new List<DiffTimelineCoreItem>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var kindLabel = kindLabelResolver(entry.Kind.ToString());
            var fieldLabel = fieldLabelResolver(entry.Field);
            var pathLabel = pathLabelResolver(entry.Scope);
            var semanticCategory = DiffTimelineDisplayLabelResolver.ToSemanticCategory(kindLabel, fieldLabel);
            var groupKey = options.GroupResolver is null
                ? DiffTimelineGroupResolver.ResolveGroupKey(CreateDraftItem(i, entry, kindLabel, fieldLabel, pathLabel, semanticCategory, valueDisplayResolver))
                : string.Empty;

            var draft = CreateDraftItem(i, entry, kindLabel, fieldLabel, pathLabel, semanticCategory, valueDisplayResolver);
            var resolvedGroupKey = options.GroupResolver?.Invoke(draft) ?? groupKey;
            var finalized = draft with
            {
                GroupKey = resolvedGroupKey,
                FilterKey = DiffTimelineFilterResolver.ResolveFilterKey(draft with { GroupKey = resolvedGroupKey }),
            };
            allItems.Add(finalized);
        }

        var filter = options.ItemFilter ?? DiffTimelineFilterResolver.BuildPassThroughFilter();
        var filtered = allItems.Where(filter).ToList();

        var groupDisplayResolver = options.GroupDisplayLabelResolver ?? DiffTimelineGroupResolver.ResolveGroupDisplayLabel;
        var groups = filtered
            .GroupBy(x => x.GroupKey)
            .OrderByDescending(x => x.Count())
            .Select(g => new DiffTimelineCoreGroup(
                GroupKey: g.Key,
                GroupDisplayLabel: groupDisplayResolver(g.First()),
                ItemIds: g.Select(x => x.Id).ToList(),
                Count: g.Count()))
            .ToList();

        var snapshot = new DiffTimelineCoreSnapshot(filtered);
        var optionSnapshot = BuildOptionSnapshot(options);
        var summary = DiffTimelineSummaryBuilder.Build(allItems, filtered, groups, optionSnapshot);
        var provisional = new DiffTimelineCoreResult(snapshot, groups, summary, new DiffTimelineCoreRowSet([], new Dictionary<string, int>(), new Dictionary<string, int>()));
        var rowSet = DiffTimelineCoreRowBuilder.BuildRows(provisional);
        return provisional with { RowSet = rowSet };
    }

    private static DiffTimelineCoreItem CreateDraftItem(
        int index,
        YmmProjectDiffEntry entry,
        string kindLabel,
        string fieldLabel,
        string pathLabel,
        string semanticCategory,
        Func<object?, string> valueDisplayResolver)
    {
        var id = $"diff-{index}";
        var displayLabel = DiffTimelineDisplayLabelResolver.ToDisplayLabel(kindLabel, fieldLabel);
        var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kindRaw"] = entry.Kind.ToString(),
            ["fieldRaw"] = entry.Field?.ToString() ?? string.Empty,
            ["scopeRaw"] = entry.Scope,
            ["semanticCategory"] = semanticCategory,
        };

        return new DiffTimelineCoreItem(
            Id: id,
            KindLabel: kindLabel,
            FieldLabel: fieldLabel,
            Category: entry.Category,
            SemanticCategory: semanticCategory,
            ScopeLabel: entry.Scope,
            PathLabel: pathLabel,
            DisplayLabel: displayLabel,
            GroupKey: string.Empty,
            FilterKey: string.Empty,
            TimelineIndex: entry.TimelineIndex,
            Layer: entry.Layer,
            Frame: entry.Frame,
            Length: Math.Max(1, entry.Length),
            OldValue: valueDisplayResolver(entry.Before),
            NewValue: valueDisplayResolver(entry.After),
            DiagnosticsMetadata: diagnostics);
    }

    private static IReadOnlyDictionary<string, string> BuildOptionSnapshot(DiffTimelineCoreBuildOptions options)
    {
        var baseSnapshot = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kindLabelResolver"] = (options.KindLabelResolver ?? DiffTimelineDisplayLabelResolver.ToDiffKindLabel).Method.Name,
            ["fieldLabelResolver"] = (options.FieldLabelResolver ?? DiffTimelineDisplayLabelResolver.ToFieldLabel).Method.Name,
            ["pathLabelResolver"] = (options.PathLabelResolver ?? DiffTimelineDisplayLabelResolver.ToPathLabel).Method.Name,
            ["valueDisplayResolver"] = (options.ValueDisplayResolver ?? (x => x?.ToString() ?? string.Empty)).Method.Name,
            ["itemFilter"] = (options.ItemFilter ?? DiffTimelineFilterResolver.BuildPassThroughFilter()).Method.Name,
            ["groupResolver"] = (options.GroupResolver ?? DiffTimelineGroupResolver.ResolveGroupKey).Method.Name,
            ["groupDisplayLabelResolver"] = (options.GroupDisplayLabelResolver ?? DiffTimelineGroupResolver.ResolveGroupDisplayLabel).Method.Name,
        };

        if (options.OptionSnapshot is not null)
        {
            foreach (var kv in options.OptionSnapshot)
            {
                baseSnapshot[kv.Key] = kv.Value;
            }
        }

        return baseSnapshot;
    }
}
