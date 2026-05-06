namespace YMMProjectManager.Presentation.Timeline;

public sealed class PureTimelineExperimentalOptions
{
    public bool EnableExperimentalYmmTimelineHost { get; set; } = false;

    public bool UseReflection { get; set; } = true;

    public bool OpenIsolatedHostWindow { get; set; } = true;

    public bool AllowViewModelGenerationAttempt { get; set; } = false;

    public int MinimumReadinessScoreForGeneration { get; set; } = 80;

    public bool DisposeImmediatelyAfterGeneration { get; set; } = true;
}
