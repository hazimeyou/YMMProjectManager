namespace YMMProjectManager.Presentation.Timeline;

public sealed class PureTimelineExperimentalOptions
{
    public bool EnableExperimentalYmmTimelineHost { get; set; } = false;

    public bool UseReflection { get; set; } = true;

    public bool OpenIsolatedHostWindow { get; set; } = true;

    public bool AllowViewModelGenerationAttempt { get; set; } = false;
    public bool AllowTimelineViewGenerationAttempt { get; set; } = false;

    public int MinimumReadinessScoreForGeneration { get; set; } = 80;

    public bool DisposeImmediatelyAfterGeneration { get; set; } = true;
    public bool ForbidVisualTreeAttach { get; set; } = true;
    public bool AllowPassiveVisualTreeParticipation { get; set; } = false;
    public bool AllowControlledLifecycleObservation { get; set; } = false;
    public int PassiveAttachHoldMs { get; set; } = 100;
    public bool AllowOffscreenHostInvestigation { get; set; } = false;

    public bool AllowSmallVisibleHostAttempt { get; set; } = false;
    public bool ManualApprovalForSmallVisibleHost { get; set; } = false;
    public bool AllowProjectDiffWindowPreintegrationAttempt { get; set; } = true;
    public bool ManualApprovalForProjectDiffWindowPreintegration { get; set; } = true;
}
