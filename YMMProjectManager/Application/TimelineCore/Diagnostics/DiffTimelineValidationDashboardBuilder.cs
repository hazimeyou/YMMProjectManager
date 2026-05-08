namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineValidationDashboardBuilder
{
    public static DiffTimelineValidationDashboard Build(
        DiffTimelineRouteValidationReport report,
        DiffTimelinePromotionTrendReadiness trendReadiness,
        DiffTimelineStandaloneRollbackGuardResult rollbackGuard,
        DiffTimelineValidationRunHistory history)
    {
        var latest = history.Runs.OrderByDescending(x => x.Timestamp).FirstOrDefault();
        var mergedWarnings = report.Warnings.Concat(rollbackGuard.Warnings).Distinct(StringComparer.Ordinal).ToList();
        var mergedBlockers = report.Blockers.Concat(rollbackGuard.Blockers).Distinct(StringComparer.Ordinal).ToList();

        return new DiffTimelineValidationDashboard(
            SelectedRoute: report.SelectedRoute,
            RequestedRoute: report.RequestedRoute,
            GateAllowed: report.GateAllowed && rollbackGuard.Allowed,
            GateReason: rollbackGuard.Reason,
            TrendCanPromote: trendReadiness.CanPromote,
            RegressionDetected: trendReadiness.LatestRegression.HasRegression,
            RegressionSummary: trendReadiness.LatestRegression.Summary,
            CacheStatus: report.CacheStatus,
            Confidence: report.ComparerResult.KeyMatchRate,
            Blockers: mergedBlockers,
            Warnings: mergedWarnings,
            Recommendation: rollbackGuard.Allowed ? report.FinalRecommendation : "stay-legacy-route",
            DiagnosticsPath: report.DiagnosticsPath,
            LatestRunAt: latest?.Timestamp);
    }
}
