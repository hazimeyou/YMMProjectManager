namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelinePreviewReadinessChecker
{
    public static DiffTimelinePreviewReadiness Evaluate(
        DiffTimelineStandaloneConfig config,
        DiffTimelineStandaloneRollbackGuardResult rollbackGuard,
        DiffTimelineDiagnosticsExportPackageResult exportPackage,
        DiffTimelinePromotionTrendReadiness trend,
        DiffTimelineValidationDashboard dashboard,
        DiffTimelineStandaloneSelfCheckResult selfCheck,
        string docsPath)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();

        if (config.StandaloneRouteEnabled)
        {
            blockers.Add("default-disabled-violated");
        }

        if (!rollbackGuard.Allowed)
        {
            blockers.Add("rollback-guard-blocked");
        }

        if (!exportPackage.Succeeded || string.IsNullOrWhiteSpace(exportPackage.ManifestPath))
        {
            blockers.Add("diagnostics-export-unavailable");
        }

        if (!trend.CanPromote)
        {
            warnings.Add("trend-not-ready");
        }

        if (!selfCheck.Assertions.TryGetValue("jsonRoundTrip", out var roundTrip) || !string.Equals(roundTrip, bool.TrueString, StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add("self-check-failed-roundtrip");
        }

        var hasDefaultRouteSafety =
            (selfCheck.Assertions.TryGetValue("defaultDisabledSafety", out var defaultRouteSafety) && string.Equals(defaultRouteSafety, bool.TrueString, StringComparison.OrdinalIgnoreCase))
            || (selfCheck.Assertions.TryGetValue("configDefaultSafety", out var defaultSafety) && string.Equals(defaultSafety, bool.TrueString, StringComparison.OrdinalIgnoreCase));
        if (!hasDefaultRouteSafety)
        {
            blockers.Add("self-check-default-safety-failed");
        }

        if (!File.Exists(docsPath))
        {
            blockers.Add("docs-missing");
        }

        var canPreview = blockers.Count == 0;
        var recommendation = canPreview ? "preview-opt-in-allowed" : "keep-shadow-validation";

        return new DiffTimelinePreviewReadiness(
            CanPreview: canPreview,
            Blockers: blockers,
            Warnings: warnings,
            RequiredEnvironmentFlags: [
                "YMM_STANDALONE_DIFFTIMELINE_ROUTE=1",
                "YMM_STANDALONE_SHADOW_VALIDATION=1 (recommended)",
            ],
            RollbackConditions: [
                "promotion-gate-blocked",
                "rollback-guard-blocked",
                "diagnostics-incomplete",
                "regression-detected",
                "cache-anomaly-detected",
            ],
            DiagnosticsExportPath: exportPackage.ExportDirectory,
            LatestDashboardSummary: dashboard,
            Recommendation: recommendation);
    }
}
