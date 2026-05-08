namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelinePreviewReadiness(
    bool CanPreview,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> RequiredEnvironmentFlags,
    IReadOnlyList<string> RollbackConditions,
    string DiagnosticsExportPath,
    DiffTimelineValidationDashboard LatestDashboardSummary,
    string Recommendation);
