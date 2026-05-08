namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelinePromotionReadinessEvaluator
{
    public static DiffTimelineStandalonePromotionReadiness Evaluate(
        DiffTimelineValidationComparerResult comparer,
        DiffTimelineStandalonePipelineEnvelope envelope)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();

        if (!envelope.IsSuccess)
        {
            blockers.Add("pipeline-not-success");
        }

        if (comparer.KeyMatchRate < 0.80)
        {
            blockers.Add("key-match-rate-low");
        }
        else if (comparer.KeyMatchRate < 0.95)
        {
            warnings.Add("key-match-rate-medium");
        }

        if (comparer.MissingFromStandaloneCount > 0)
        {
            blockers.Add("missing-rows-exist");
        }

        if (comparer.ExtraInStandaloneCount > 0)
        {
            warnings.Add("extra-rows-exist");
        }

        if (envelope.CacheHit)
        {
            warnings.Add("cache-hit-validation");
        }

        var confidence = Math.Clamp(comparer.KeyMatchRate, 0.0, 1.0);
        return new DiffTimelineStandalonePromotionReadiness(
            CanPromote: blockers.Count == 0,
            Blockers: blockers,
            Warnings: warnings,
            Confidence: confidence,
            CacheStatus: envelope.CacheHit ? "hit" : "miss",
            FallbackReason: envelope.FallbackReason,
            ComparerResult: comparer);
    }
}
