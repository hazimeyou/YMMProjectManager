namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineValidationRegressionDetector
{
    public static DiffTimelineValidationRegressionResult Detect(
        DiffTimelineValidationRunRecord latest,
        DiffTimelineValidationRunRecord? previous)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();

        if (previous is not null)
        {
            if (latest.ComparerConfidence < previous.ComparerConfidence)
            {
                warnings.Add("confidence-dropped");
            }

            if (latest.Blockers.Count > previous.Blockers.Count)
            {
                blockers.Add("blockers-increased");
            }

            if (latest.FallbackReason != "none" && previous.FallbackReason == "none")
            {
                warnings.Add("fallback-increased");
            }

            if (!latest.CacheHit && previous.CacheHit)
            {
                warnings.Add("cache-behavior-changed");
            }

            if (string.IsNullOrWhiteSpace(latest.DiagnosticsPath))
            {
                blockers.Add("diagnostics-incomplete");
            }
        }

        var hasRegression = blockers.Count > 0 || warnings.Count > 0;
        var summary = hasRegression
            ? string.Join(",", blockers.Concat(warnings))
            : "no-regression";
        return new DiffTimelineValidationRegressionResult(hasRegression, blockers, warnings, summary);
    }

    public static DiffTimelinePromotionTrendReadiness EvaluateTrend(DiffTimelineValidationRunHistory history)
    {
        var runs = history.Runs.OrderBy(x => x.Timestamp).ToList();
        if (runs.Count == 0)
        {
            return new DiffTimelinePromotionTrendReadiness(false, 0, 0, new DiffTimelineValidationRegressionResult(false, [], [], "no-runs"), "collect-more-runs");
        }

        var stable = runs.Count(x => x.GateAllowed && x.Blockers.Count == 0);
        var consecutive = 0;
        for (var i = runs.Count - 1; i >= 0; i--)
        {
            if (runs[i].GateAllowed && runs[i].Blockers.Count == 0)
            {
                consecutive++;
            }
            else
            {
                break;
            }
        }

        var latest = runs[^1];
        var previous = runs.Count >= 2 ? runs[^2] : null;
        var regression = Detect(latest, previous);
        var canPromote = consecutive >= 3 && !regression.HasRegression;
        var recommendation = canPromote ? "promotion-candidate" : "keep-shadow-validation";
        return new DiffTimelinePromotionTrendReadiness(canPromote, stable, consecutive, regression, recommendation);
    }
}
