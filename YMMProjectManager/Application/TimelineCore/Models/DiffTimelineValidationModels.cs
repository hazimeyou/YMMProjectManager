namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelineExistingRouteSummary(
    int ItemCount,
    int GroupCount,
    int AddedCount,
    int RemovedCount,
    int ChangedCount,
    IReadOnlyList<string> Keys);

public sealed record DiffTimelineValidationComparerResult(
    int ExistingItemCount,
    int StandaloneItemCount,
    int ExistingGroupCount,
    int StandaloneGroupCount,
    int ExistingAddedCount,
    int StandaloneAddedCount,
    int ExistingRemovedCount,
    int StandaloneRemovedCount,
    int ExistingChangedCount,
    int StandaloneChangedCount,
    int CommonKeyCount,
    int MissingFromStandaloneCount,
    int ExtraInStandaloneCount,
    double KeyMatchRate,
    IReadOnlyList<string> MissingKeys,
    IReadOnlyList<string> ExtraKeys,
    IReadOnlyList<string> Reasons);

public sealed record DiffTimelineStandalonePromotionReadiness(
    bool CanPromote,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    double Confidence,
    string CacheStatus,
    string FallbackReason,
    DiffTimelineValidationComparerResult ComparerResult);
