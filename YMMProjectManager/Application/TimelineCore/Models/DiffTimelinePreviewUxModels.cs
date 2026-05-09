namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelineSearchQuery(
    string Text,
    bool CaseSensitive = false,
    bool Regex = false);

public sealed record DiffTimelineFilterState(
    IReadOnlyList<string> PathFilters,
    IReadOnlyList<string> SemanticCategoryFilters,
    IReadOnlyList<string> ChangeTypeFilters,
    IReadOnlyList<string> GroupFilters,
    DiffTimelineSearchQuery? SearchQuery,
    bool ChangedOnly,
    bool WarningOnly);

public sealed record DiffTimelineFilteredResult(
    DiffTimelineCoreRowSet RowSet,
    int MatchedRowCount,
    int FilteredOutCount,
    IReadOnlyDictionary<string, string> ActiveFilters,
    IReadOnlyDictionary<string, int> SeveritySummary);

public sealed record DiffTimelineGroupState(
    string GroupKey,
    string GroupDisplayLabel,
    bool Collapsed,
    int RowCount,
    IReadOnlyDictionary<string, int> SemanticSummary,
    IReadOnlyDictionary<string, int> SeveritySummary);

public sealed record DiffTimelineSemanticUxMetadata(
    string SemanticBadge,
    string ConfidenceDisplay,
    string GroupedEditKey,
    string RelationKind);

public sealed record DiffTimelineRowUxMetadata(
    bool CompactReady,
    string IconKey,
    string HighlightKey,
    string NavigationKey,
    bool StickyGroupReady);

public sealed record DiffTimelineSnapshotListItem(
    string SnapshotHash,
    string SnapshotName,
    DateTimeOffset CreatedAt,
    string SourceProject,
    string Note,
    IReadOnlyList<string> Tags,
    int TimelineCount,
    int ItemCount);

public sealed record DiffTimelineComparisonCandidate(
    string OldSnapshotHash,
    string NewSnapshotHash,
    string Label,
    DateTimeOffset CreatedAt);

public sealed record DiffTimelineSnapshotDetailSummary(
    string SnapshotHash,
    int TimelineCount,
    int LayerCount,
    int ItemCount,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record DiffTimelineCompareRequest(
    string OldSnapshotHash,
    string NewSnapshotHash,
    IReadOnlyDictionary<string, string> CompareOptions,
    IReadOnlyDictionary<string, string> DiagnosticsOptions);

public sealed record DiffTimelineSnapshotBrowserState(
    IReadOnlyList<DiffTimelineSnapshotListItem> Snapshots,
    IReadOnlyList<DiffTimelineComparisonCandidate> ComparisonCandidates,
    DiffTimelineSnapshotDetailSummary? SelectedSnapshotDetail,
    string LatestValidationState);

public sealed record DiffTimelineSnapshotRepositoryEntry(
    DiffTimelineProjectSnapshot Snapshot,
    string Name,
    string SourceProject,
    string Note,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt);

public sealed record DiffTimelineSnapshotRetentionPlan(
    int KeepLatestCount,
    IReadOnlyList<string> CleanupCandidateHashes,
    string Recommendation);

public sealed record DiffTimelineComparisonHistoryEntry(
    string OldSnapshotHash,
    string NewSnapshotHash,
    DateTimeOffset ComparedAt,
    string Summary,
    IReadOnlyDictionary<string, string> Metadata);
