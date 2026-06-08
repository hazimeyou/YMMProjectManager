namespace YMMProjectManager.Infrastructure.Thumbnails;

public sealed record ThumbnailGenerationDiagnostics
{
    public bool FastThumbnailEnabled { get; init; }

    public bool TimelineFound { get; init; }

    public bool PreviewViewModelFound { get; init; }

    public bool GetBitmapFound { get; init; }

    public int SampleCount { get; init; }

    public int CapturedCount { get; init; }

    public int FailedFrameCount { get; init; }

    public int RetryCount { get; init; }

    public TimeSpan AverageSeekDuration { get; init; }

    public TimeSpan AverageCaptureDuration { get; init; }

    public TimeSpan TotalDuration { get; init; }

    public string? FallbackReason { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
