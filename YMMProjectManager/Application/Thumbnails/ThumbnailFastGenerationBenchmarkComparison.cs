namespace YMMProjectManager.Application.Thumbnails;

public sealed class ThumbnailFastGenerationBenchmarkComparison
{
    public bool LegacyMeasured { get; init; }

    public int? SampleCount { get; init; }

    public int? SeekSettleDelayMilliseconds { get; init; }

    public long? LegacyTotalDurationMs { get; init; }

    public long? FastTotalDurationMs { get; init; }

    public double? SpeedupRatio { get; init; }

    public string? Reason { get; init; }

    public static double? CalculateSpeedupRatio(long? legacyTotalDurationMs, long? fastTotalDurationMs)
    {
        if (legacyTotalDurationMs is null || legacyTotalDurationMs <= 0)
        {
            return null;
        }

        if (fastTotalDurationMs is null || fastTotalDurationMs <= 0)
        {
            return null;
        }

        return legacyTotalDurationMs.Value / (double)fastTotalDurationMs.Value;
    }
}
