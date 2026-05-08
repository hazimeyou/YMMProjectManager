namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelineStandaloneConfig(
    bool ShadowValidationEnabled,
    bool StandaloneRouteEnabled,
    string DiagnosticsVerbosity,
    int HistoryKeepCount,
    bool EnableGatePolicyOverride,
    int ConsecutiveFailureThreshold,
    bool StrictRegressionBlocking,
    bool StrictDiagnosticsCompleteness,
    bool StrictCacheAnomalyBlocking)
{
    public IReadOnlyDictionary<string, string> ToEnvironmentSnapshot()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["YMM_STANDALONE_SHADOW_VALIDATION"] = ShadowValidationEnabled ? "1" : "0",
            ["YMM_STANDALONE_DIFFTIMELINE_ROUTE"] = StandaloneRouteEnabled ? "1" : "0",
            ["YMM_STANDALONE_DIAGNOSTICS_VERBOSITY"] = DiagnosticsVerbosity,
            ["YMM_STANDALONE_HISTORY_KEEP_COUNT"] = HistoryKeepCount.ToString(),
            ["YMM_STANDALONE_GATE_POLICY_OVERRIDE"] = EnableGatePolicyOverride ? "1" : "0",
            ["YMM_STANDALONE_CONSECUTIVE_FAILURE_THRESHOLD"] = ConsecutiveFailureThreshold.ToString(),
            ["YMM_STANDALONE_STRICT_REGRESSION_BLOCKING"] = StrictRegressionBlocking ? "1" : "0",
            ["YMM_STANDALONE_STRICT_DIAGNOSTICS_COMPLETENESS"] = StrictDiagnosticsCompleteness ? "1" : "0",
            ["YMM_STANDALONE_STRICT_CACHE_ANOMALY_BLOCKING"] = StrictCacheAnomalyBlocking ? "1" : "0",
        };
    }
}

public sealed record DiffTimelineStandaloneRollbackGuardResult(
    bool Allowed,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    string Reason);

public sealed record DiffTimelineValidationDashboard(
    string SelectedRoute,
    string RequestedRoute,
    bool GateAllowed,
    string GateReason,
    bool TrendCanPromote,
    bool RegressionDetected,
    string RegressionSummary,
    string CacheStatus,
    double Confidence,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    string Recommendation,
    string DiagnosticsPath,
    DateTimeOffset? LatestRunAt);

public sealed record DiffTimelineDiagnosticsExportPackageResult(
    bool Succeeded,
    string ExportDirectory,
    string ManifestPath,
    IReadOnlyList<string> ExportedFiles,
    IReadOnlyList<string> Warnings);
