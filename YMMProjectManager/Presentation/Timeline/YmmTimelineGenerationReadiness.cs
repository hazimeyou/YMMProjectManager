namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmTimelineGenerationReadiness
{
    public bool TimelineViewTypeFound { get; set; }

    public bool TimelineViewModelTypeFound { get; set; }

    public bool TimelineViewConstructorBindable { get; set; }

    public bool TimelineViewModelConstructorBindable { get; set; }

    public bool CanAttemptViewModelGeneration { get; set; }

    public bool CanAttemptViewGeneration { get; set; }

    public int Score { get; set; }

    public IReadOnlyList<string> BlockingReasons { get; set; } = [];

    public IReadOnlyList<string> Warnings { get; set; } = [];
}
