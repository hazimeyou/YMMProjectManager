namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelineValidationRunRecord(
    DateTimeOffset Timestamp,
    string ProjectIdentity,
    string OldSnapshotHash,
    string NewSnapshotHash,
    string RequestedRoute,
    string SelectedRoute,
    bool GateAllowed,
    string GateReason,
    double ComparerConfidence,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    bool CacheHit,
    string DiagnosticsPath,
    string FinalRecommendation,
    string FallbackReason);

public sealed record DiffTimelineValidationRunHistory(
    IReadOnlyList<DiffTimelineValidationRunRecord> Runs);

public sealed record DiffTimelineValidationRegressionResult(
    bool HasRegression,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    string Summary);

public sealed record DiffTimelinePromotionTrendReadiness(
    bool CanPromote,
    int StableRunCount,
    int ConsecutiveSuccessCount,
    DiffTimelineValidationRegressionResult LatestRegression,
    string Recommendation);
