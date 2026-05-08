namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelineRouteSelectionResult(
    string RequestedRoute,
    string SelectedRoute,
    string FallbackRoute,
    string Reason,
    DiffTimelineStandalonePromotionReadiness? PromotionReadiness,
    string DiagnosticsPath);

public static class DiffTimelineStandalonePromotionGate
{
    public static (bool Allowed, string Reason, IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings) Evaluate(
        DiffTimelineStandalonePromotionReadiness readiness,
        DiffTimelinePromotionGatePolicy? policy = null)
    {
        policy ??= new DiffTimelinePromotionGatePolicy(
            MinConfidence: 0.95,
            MaxMissingRows: 0,
            MaxExtraRows: 20,
            MaxRowCountDifference: 20,
            RequireDiagnosticsCompleteness: true,
            RequireFallbackReason: true);

        var blockers = new List<string>(readiness.Blockers);
        var warnings = new List<string>(readiness.Warnings);
        var comparer = readiness.ComparerResult;

        if (readiness.Confidence < policy.MinConfidence)
        {
            blockers.Add("confidence-below-threshold");
        }

        if (comparer.MissingFromStandaloneCount > policy.MaxMissingRows)
        {
            blockers.Add("missing-rows-over-threshold");
        }

        if (comparer.ExtraInStandaloneCount > policy.MaxExtraRows)
        {
            warnings.Add("extra-rows-over-threshold");
        }

        if (Math.Abs(comparer.ExistingItemCount - comparer.StandaloneItemCount) > policy.MaxRowCountDifference)
        {
            blockers.Add("row-count-difference-over-threshold");
        }

        if (policy.RequireDiagnosticsCompleteness)
        {
            if (string.IsNullOrWhiteSpace(readiness.CacheStatus)) blockers.Add("cache-status-missing");
            if (string.IsNullOrWhiteSpace(readiness.FallbackReason)) warnings.Add("fallback-reason-missing");
        }

        if (policy.RequireFallbackReason && string.IsNullOrWhiteSpace(readiness.FallbackReason))
        {
            warnings.Add("fallback-reason-empty");
        }

        var allowed = blockers.Count == 0 && readiness.CanPromote;
        return (allowed, allowed ? "promotion-allowed" : "promotion-blocked", blockers, warnings);
    }

    public static DiffTimelineRouteValidationReport BuildReport(
        string requestedRoute,
        string selectedRoute,
        DiffTimelineStandalonePromotionReadiness readiness,
        bool cacheHit,
        string diagnosticsPath,
        string rollbackReason,
        DiffTimelinePromotionGatePolicy? policy = null)
    {
        var gate = Evaluate(readiness, policy);
        return new DiffTimelineRouteValidationReport(
            RequestedRoute: requestedRoute,
            SelectedRoute: selectedRoute,
            GateAllowed: gate.Allowed,
            GateReason: gate.Reason,
            ComparerResult: readiness.ComparerResult,
            PromotionReadiness: readiness,
            CacheHit: cacheHit,
            CacheStatus: readiness.CacheStatus,
            DiagnosticsPath: diagnosticsPath,
            Blockers: gate.Blockers,
            Warnings: gate.Warnings,
            FinalRecommendation: gate.Allowed ? "can-promote-manual-only" : "stay-legacy-route",
            RollbackReason: rollbackReason);
    }
}
