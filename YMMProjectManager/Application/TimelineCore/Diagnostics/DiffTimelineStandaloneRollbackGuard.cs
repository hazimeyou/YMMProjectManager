namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineStandaloneRollbackGuard
{
    public static DiffTimelineStandaloneRollbackGuardResult Evaluate(
        DiffTimelineRouteValidationReport report,
        DiffTimelineValidationRunHistory history,
        DiffTimelineStandaloneConfig config,
        DiffTimelinePromotionTrendReadiness trendReadiness)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();

        if (!report.GateAllowed)
        {
            blockers.Add("promotion-gate-blocked");
        }

        if (config.StrictRegressionBlocking && trendReadiness.LatestRegression.HasRegression)
        {
            blockers.Add("regression-detected");
        }

        var consecutiveFailures = CountConsecutiveFailures(history);
        if (consecutiveFailures >= config.ConsecutiveFailureThreshold)
        {
            blockers.Add("consecutive-failure-threshold-reached");
        }

        if (config.StrictDiagnosticsCompleteness && string.IsNullOrWhiteSpace(report.DiagnosticsPath))
        {
            blockers.Add("diagnostics-incomplete");
        }

        if (config.StrictCacheAnomalyBlocking && trendReadiness.LatestRegression.Warnings.Contains("cache-behavior-changed"))
        {
            blockers.Add("cache-anomaly-detected");
        }

        if (report.Blockers.Count > 0)
        {
            warnings.Add("validation-report-has-blockers");
        }

        var allowed = blockers.Count == 0;
        var reason = allowed ? "rollback-guard-passed" : string.Join(",", blockers);
        return new DiffTimelineStandaloneRollbackGuardResult(allowed, blockers, warnings, reason);
    }

    private static int CountConsecutiveFailures(DiffTimelineValidationRunHistory history)
    {
        var ordered = history.Runs.OrderBy(x => x.Timestamp).ToList();
        var count = 0;
        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            var run = ordered[i];
            if (run.GateAllowed && run.Blockers.Count == 0)
            {
                break;
            }

            count++;
        }

        return count;
    }
}
