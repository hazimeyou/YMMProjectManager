namespace YMMProjectManager.Application.Thumbnails;

public sealed class ThumbnailFastGenerationBenchmarkSummaryReport
{
    public string? ProjectPath { get; init; }

    public DateTimeOffset GeneratedAt { get; init; }

    public string? BenchmarkDirectory { get; init; }

    public IReadOnlyList<int> SampleCounts { get; init; } = [];

    public IReadOnlyList<int> SeekSettleDelayMilliseconds { get; init; } = [];

    public int RunCount { get; init; }

    public int SuccessCount { get; init; }

    public int FailureCount { get; init; }

    public int RequestedFrameCount { get; init; }

    public int CapturedFrameCount { get; init; }

    public int FailedFrameCount { get; init; }

    public int RetryCount { get; init; }

    public double TotalDurationMs { get; init; }

    public double AverageSeekDurationMs { get; init; }

    public double AverageSettleDurationMs { get; init; }

    public double AverageCaptureDurationMs { get; init; }

    public double AverageSaveDurationMs { get; init; }

    public double? FramesPerSecondEffective { get; init; }

    public long? LegacyTotalDurationMs { get; init; }

    public double? SpeedupRatio { get; init; }

    public string? OverallFailureReason { get; init; }

    public long? InitialTotalMemoryBytes { get; init; }

    public long? FinalTotalMemoryBytes { get; init; }

    public long? MemoryDeltaBytes { get; init; }

    public long? PostGcTotalMemoryBytes { get; init; }

    public long? PostGcMemoryDeltaBytes { get; init; }
}
