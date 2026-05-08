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
        DiffTimelineStandalonePromotionReadiness readiness)
    {
        if (!readiness.CanPromote)
        {
            return (false, "promotion-blocked", readiness.Blockers, readiness.Warnings);
        }

        return (true, "promotion-allowed", readiness.Blockers, readiness.Warnings);
    }
}
