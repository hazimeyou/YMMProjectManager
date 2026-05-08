namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineStandaloneConfigResolver
{
    public static DiffTimelineStandaloneConfig ResolveFromEnvironment()
    {
        return new DiffTimelineStandaloneConfig(
            ShadowValidationEnabled: GetBool("YMM_STANDALONE_SHADOW_VALIDATION", false),
            StandaloneRouteEnabled: GetBool("YMM_STANDALONE_DIFFTIMELINE_ROUTE", false),
            DiagnosticsVerbosity: GetString("YMM_STANDALONE_DIAGNOSTICS_VERBOSITY", "standard"),
            HistoryKeepCount: GetInt("YMM_STANDALONE_HISTORY_KEEP_COUNT", 50, 10, 500),
            EnableGatePolicyOverride: GetBool("YMM_STANDALONE_GATE_POLICY_OVERRIDE", false),
            ConsecutiveFailureThreshold: GetInt("YMM_STANDALONE_CONSECUTIVE_FAILURE_THRESHOLD", 2, 1, 20),
            StrictRegressionBlocking: GetBool("YMM_STANDALONE_STRICT_REGRESSION_BLOCKING", true),
            StrictDiagnosticsCompleteness: GetBool("YMM_STANDALONE_STRICT_DIAGNOSTICS_COMPLETENESS", true),
            StrictCacheAnomalyBlocking: GetBool("YMM_STANDALONE_STRICT_CACHE_ANOMALY_BLOCKING", true));
    }

    public static DiffTimelinePromotionGatePolicy BuildPolicy(DiffTimelineStandaloneConfig config)
    {
        if (!config.EnableGatePolicyOverride)
        {
            return new DiffTimelinePromotionGatePolicy(
                MinConfidence: 0.95,
                MaxMissingRows: 0,
                MaxExtraRows: 20,
                MaxRowCountDifference: 20,
                RequireDiagnosticsCompleteness: true,
                RequireFallbackReason: true);
        }

        return new DiffTimelinePromotionGatePolicy(
            MinConfidence: 0.90,
            MaxMissingRows: 2,
            MaxExtraRows: 30,
            MaxRowCountDifference: 30,
            RequireDiagnosticsCompleteness: config.StrictDiagnosticsCompleteness,
            RequireFallbackReason: true);
    }

    private static bool GetBool(string key, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetInt(string key, int fallback, int min, int max)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (!int.TryParse(value, out var parsed))
        {
            return fallback;
        }

        return Math.Clamp(parsed, min, max);
    }

    private static string GetString(string key, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
