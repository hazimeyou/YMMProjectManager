namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineFilterSearchPipeline
{
    public static DiffTimelineFilteredResult Apply(
        DiffTimelineCoreResult result,
        DiffTimelineFilterState filterState)
    {
        var matched = new List<DiffTimelineCoreRow>();
        foreach (var row in result.RowSet.Rows)
        {
            if (!Matches(row, filterState))
            {
                continue;
            }

            matched.Add(row);
        }

        var filteredOut = result.RowSet.Rows.Count - matched.Count;
        var grouped = matched.GroupBy(x => x.GroupKey).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var semantic = matched.GroupBy(x => x.SemanticCategory).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var severity = matched
            .GroupBy(x => ResolveSeverity(x))
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);

        var rowSet = new DiffTimelineCoreRowSet(matched, grouped, semantic);
        return new DiffTimelineFilteredResult(
            RowSet: rowSet,
            MatchedRowCount: matched.Count,
            FilteredOutCount: filteredOut,
            ActiveFilters: ToActiveFilterMap(filterState),
            SeveritySummary: severity);
    }

    public static Task<DiffTimelineFilteredResult> ApplyAsync(
        DiffTimelineCoreResult result,
        DiffTimelineFilterState filterState,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Apply(result, filterState);
        }, cancellationToken);
    }

    private static bool Matches(DiffTimelineCoreRow row, DiffTimelineFilterState state)
    {
        if (state.PathFilters.Count > 0 && !state.PathFilters.Any(x => row.Path.Contains(x, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (state.SemanticCategoryFilters.Count > 0 && !state.SemanticCategoryFilters.Contains(row.SemanticCategory, StringComparer.Ordinal))
            return false;
        if (state.ChangeTypeFilters.Count > 0 && !state.ChangeTypeFilters.Contains(row.DiffKind, StringComparer.Ordinal))
            return false;
        if (state.GroupFilters.Count > 0 && !state.GroupFilters.Contains(row.GroupKey, StringComparer.Ordinal))
            return false;
        if (state.ChangedOnly && !string.Equals(row.DiffKind, "変更", StringComparison.Ordinal) && !string.Equals(row.DiffKind, "Changed", StringComparison.OrdinalIgnoreCase))
            return false;
        if (state.WarningOnly && !string.Equals(ResolveSeverity(row), "warning", StringComparison.Ordinal))
            return false;
        if (state.SearchQuery is { } query && !MatchesQuery(row, query))
            return false;
        return true;
    }

    private static bool MatchesQuery(DiffTimelineCoreRow row, DiffTimelineSearchQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.Text))
        {
            return true;
        }

        var haystack = $"{row.Title} {row.Subtitle} {row.Detail} {row.Path} {row.Field} {row.OldValue} {row.NewValue}";
        if (query.Regex)
        {
            var options = query.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            return Regex.IsMatch(haystack, query.Text, options);
        }

        return haystack.Contains(query.Text, query.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string> ToActiveFilterMap(DiffTimelineFilterState state)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pathFilter"] = string.Join(",", state.PathFilters),
            ["semanticFilter"] = string.Join(",", state.SemanticCategoryFilters),
            ["changeTypeFilter"] = string.Join(",", state.ChangeTypeFilters),
            ["groupFilter"] = string.Join(",", state.GroupFilters),
            ["search"] = state.SearchQuery?.Text ?? string.Empty,
            ["changedOnly"] = state.ChangedOnly.ToString(),
            ["warningOnly"] = state.WarningOnly.ToString(),
        };
    }

    private static string ResolveSeverity(DiffTimelineCoreRow row)
    {
        if (row.DiagnosticsMetadata.TryGetValue("severity", out var severity) && !string.IsNullOrWhiteSpace(severity))
        {
            return severity;
        }

        return row.DiffKind.Contains("削除", StringComparison.Ordinal) || row.DiffKind.Contains("Removed", StringComparison.OrdinalIgnoreCase)
            ? "warning"
            : "info";
    }
}
