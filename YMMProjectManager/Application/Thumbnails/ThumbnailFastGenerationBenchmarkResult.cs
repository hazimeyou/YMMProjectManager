namespace YMMProjectManager.Application.Thumbnails;

public sealed class ThumbnailFastGenerationBenchmarkResult
{
    public string? ProjectPath { get; init; }

    public DateTimeOffset GeneratedAt { get; init; }

    public string? BenchmarkDirectory { get; init; }

    public string? BenchmarkFilePath { get; init; }

    public string? SummaryFilePath { get; init; }

    public string? OverallFailureReason { get; init; }

    public IReadOnlyList<ThumbnailFastGenerationBenchmarkRunResult> Runs { get; init; } = [];

    public ThumbnailFastGenerationBenchmarkSummary Summary { get; init; } = new();

    public ThumbnailFastGenerationBenchmarkComparison? LegacyComparison { get; init; }

    public IReadOnlyList<string> GeneratedFiles { get; init; } = [];
}

public sealed class ThumbnailFastGenerationBenchmarkRunResult
{
    public string? ProjectPath { get; init; }

    public int SampleCount { get; init; }

    public int RequestedFrameCount { get; init; }

    public int CapturedFrameCount { get; init; }

    public int FailedFrameCount { get; init; }

    public int RetryCount { get; init; }

    public double TotalDurationMs { get; init; }

    public double AverageSeekDurationMs { get; init; }

    public double AverageSettleDurationMs { get; init; }

    public double AverageCaptureDurationMs { get; init; }

    public double AverageSaveDurationMs { get; init; }

    public double? MinCaptureDurationMs { get; init; }

    public double? MaxCaptureDurationMs { get; init; }

    public double? MinSeekDurationMs { get; init; }

    public double? MaxSeekDurationMs { get; init; }

    public double? MinSettleDurationMs { get; init; }

    public double? MaxSettleDurationMs { get; init; }

    public double? MinSaveDurationMs { get; init; }

    public double? MaxSaveDurationMs { get; init; }

    public int SeekMeasurementCount { get; init; }

    public int SettleMeasurementCount { get; init; }

    public int CaptureMeasurementCount { get; init; }

    public int SaveMeasurementCount { get; init; }

    public double? FramesPerSecondEffective { get; init; }

    public bool FallbackUsed { get; init; }

    public string? FallbackReason { get; init; }

    public string? PreferredGetBitmapCall { get; init; }

    public int? BitmapWidth { get; init; }

    public int? BitmapHeight { get; init; }

    public string? BitmapPixelFormat { get; init; }

    public int SavedFrameCount { get; init; }

    public DateTimeOffset GeneratedAt { get; init; }

    public string? OutputDirectory { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class ThumbnailFastGenerationBenchmarkSummary
{
    public int RunCount { get; init; }

    public int SuccessCount { get; init; }

    public int FailureCount { get; init; }

    public double TotalDurationMs { get; init; }

    public long? LegacyTotalDurationMs { get; init; }

    public double? SpeedupRatio { get; init; }

    public long? InitialTotalMemoryBytes { get; init; }

    public long? FinalTotalMemoryBytes { get; init; }

    public long? MemoryDeltaBytes { get; init; }

    public long? PostGcTotalMemoryBytes { get; init; }

    public long? PostGcMemoryDeltaBytes { get; init; }
}
