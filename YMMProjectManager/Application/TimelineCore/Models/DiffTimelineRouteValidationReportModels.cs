namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelinePromotionGatePolicy(
    double MinConfidence,
    int MaxMissingRows,
    int MaxExtraRows,
    int MaxRowCountDifference,
    bool RequireDiagnosticsCompleteness,
    bool RequireFallbackReason);

public sealed record DiffTimelineRouteValidationReport(
    string RequestedRoute,
    string SelectedRoute,
    bool GateAllowed,
    string GateReason,
    DiffTimelineValidationComparerResult ComparerResult,
    DiffTimelineStandalonePromotionReadiness PromotionReadiness,
    bool CacheHit,
    string CacheStatus,
    string DiagnosticsPath,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    string FinalRecommendation,
    string RollbackReason);
