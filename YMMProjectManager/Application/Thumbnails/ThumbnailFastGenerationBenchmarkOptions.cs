namespace YMMProjectManager.Application.Thumbnails;

public sealed class ThumbnailFastGenerationBenchmarkOptions
{
    public IReadOnlyList<int> SampleCounts { get; init; } = [16, 32, 64, 128, 256];

    public IReadOnlyList<int> SeekSettleDelayMilliseconds { get; init; } = [0, 25, 50, 100];

    public int MaxRetryCount { get; init; } = 3;

    public bool PersistAllFrames { get; init; }

    public bool IncludeLegacyComparison { get; init; }

    public int LegacyComparisonSampleCount { get; init; } = 64;

    public int LegacyComparisonSeekSettleDelayMilliseconds { get; init; } = 50;

    public string PreferredGetBitmapCall { get; init; } = "GetBitmap(true)";
}
