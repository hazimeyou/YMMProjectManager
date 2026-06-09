namespace YMMProjectManager.Application.Thumbnails;

public sealed class CurrentPreviewCaptureResult
{
    public DateTimeOffset Timestamp { get; init; }

    public bool Success { get; init; }

    public string? FailureReason { get; init; }

    public int WindowCount { get; init; }

    public int VisualTreeElementCount { get; init; }

    public bool PreviewViewFound { get; init; }

    public bool PreviewViewModelFound { get; init; }

    public bool GetBitmapMethodFound { get; init; }

    public IReadOnlyList<string> GetBitmapParameterTypes { get; init; } = [];

    public string? NextRecommendedCall { get; init; }

    public bool InvocationSucceeded { get; init; }

    public bool CaptureSucceeded { get; init; }

    public int? BitmapWidth { get; init; }

    public int? BitmapHeight { get; init; }

    public string? BitmapPixelFormat { get; init; }

    public string? SavedPath { get; init; }

    public string? DiagnosticsPath { get; init; }

    public double DurationMs { get; init; }
}
