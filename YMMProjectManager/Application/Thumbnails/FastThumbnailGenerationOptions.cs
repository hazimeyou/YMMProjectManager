namespace YMMProjectManager.Application.Thumbnails;

public sealed class FastThumbnailGenerationOptions
{
    public bool Enabled { get; init; }

    public int SampleCount { get; init; } = 64;

    public int SeekSettleDelayMilliseconds { get; init; } = 50;

    public int MaxRetryCount { get; init; } = 3;

    public bool AllowClipboardFallback { get; init; }

    public bool AllowScreenCaptureFallback { get; init; }
}
