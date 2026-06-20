namespace YMMProjectManager.Application.Thumbnails;

public sealed class FastThumbnailOptions
{
    public bool IsEnabled { get; set; } = true;
    public int SampleCount { get; set; } = 64;
    public int MaxRetryCount { get; set; } = 3;
    public int SeekSettleDelayMilliseconds { get; set; } = 50;
    public bool FallbackToLegacyOnFailure { get; set; } = true;
    public string PreferredGetBitmapCall { get; set; } = "GetBitmap(true)";
}

public sealed class FastThumbnailGenerationOptions
{
    public bool Enabled { get; set; }
    public int SampleCount { get; set; } = 64;
    public int SeekSettleDelayMilliseconds { get; set; } = 50;
    public int MaxRetryCount { get; set; } = 3;
    public bool AllowClipboardFallback { get; set; }
    public bool AllowScreenCaptureFallback { get; set; }
}

public static class FastThumbnailFrameSampler
{
    public static int[] CreateSampleFrames(int sampleCount, int firstFrame, int lastFrame)
    {
        if (sampleCount <= 0)
        {
            return [];
        }

        if (sampleCount == 1)
        {
            return [Math.Max(0, firstFrame)];
        }

        var start = Math.Max(0, firstFrame);
        var end = Math.Max(start, lastFrame);
        var frames = new int[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            frames[i] = (int)Math.Round(start + ((end - start) * (i / (double)(sampleCount - 1))));
        }

        return frames;
    }
}

public sealed class ThumbnailGenerationDiagnostics
{
    public bool FastThumbnailEnabled { get; set; }
    public bool TimelineFound { get; set; }
    public bool PreviewViewModelFound { get; set; }
    public bool GetBitmapFound { get; set; }
    public int SampleCount { get; set; }
    public int CapturedCount { get; set; }
    public int FailedFrameCount { get; set; }
    public int RetryCount { get; set; }
    public TimeSpan AverageSeekDuration { get; set; }
    public TimeSpan AverageCaptureDuration { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public string? FallbackReason { get; set; }
    public List<string> Warnings { get; set; } = [];
}

public sealed class FastThumbnailGenerationResult
{
    public bool Success { get; set; }
    public int RequestedSampleCount { get; set; }
    public int CapturedCount { get; set; }
    public TimeSpan Duration { get; set; }
    public string? FallbackReason { get; set; }
    public List<string> Warnings { get; set; } = [];
    public ThumbnailGenerationDiagnostics? Diagnostics { get; set; }
}

public sealed class ThumbnailFastGenerationBenchmarkOptions
{
    public List<int> SampleCounts { get; set; } = [16, 32, 64, 128, 256];
    public List<int> SeekSettleDelayMilliseconds { get; set; } = [0, 25, 50, 100];
    public int MaxRetryCount { get; set; } = 3;
    public bool PersistAllFrames { get; set; }
    public bool IncludeLegacyComparison { get; set; }
    public string PreferredGetBitmapCall { get; set; } = "GetBitmap(true)";
}

public sealed class ThumbnailFastGenerationBenchmarkRunResult
{
    public string ProjectPath { get; set; } = string.Empty;
    public int SampleCount { get; set; }
    public int RequestedFrameCount { get; set; }
    public int CapturedFrameCount { get; set; }
    public int FailedFrameCount { get; set; }
    public int RetryCount { get; set; }
    public double TotalDurationMs { get; set; }
    public double AverageSeekDurationMs { get; set; }
    public double AverageSettleDurationMs { get; set; }
    public double AverageCaptureDurationMs { get; set; }
    public double AverageSaveDurationMs { get; set; }
    public double? MinCaptureDurationMs { get; set; }
    public double? MaxCaptureDurationMs { get; set; }
    public double? MinSeekDurationMs { get; set; }
    public double? MaxSeekDurationMs { get; set; }
    public double? MinSettleDurationMs { get; set; }
    public double? MaxSettleDurationMs { get; set; }
    public double? MinSaveDurationMs { get; set; }
    public double? MaxSaveDurationMs { get; set; }
    public int SeekMeasurementCount { get; set; }
    public int SettleMeasurementCount { get; set; }
    public int CaptureMeasurementCount { get; set; }
    public int SaveMeasurementCount { get; set; }
    public double? FramesPerSecondEffective { get; set; }
    public bool FallbackUsed { get; set; }
    public string? FallbackReason { get; set; }
    public string PreferredGetBitmapCall { get; set; } = string.Empty;
    public int? BitmapWidth { get; set; }
    public int? BitmapHeight { get; set; }
    public string? BitmapPixelFormat { get; set; }
    public int SavedFrameCount { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public string OutputDirectory { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = [];
}

public sealed class ThumbnailFastGenerationBenchmarkSummary
{
    public int RunCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public double TotalDurationMs { get; set; }
    public double? LegacyTotalDurationMs { get; set; }
    public double? SpeedupRatio { get; set; }
    public long InitialTotalMemoryBytes { get; set; }
    public long FinalTotalMemoryBytes { get; set; }
    public long MemoryDeltaBytes { get; set; }
    public long PostGcTotalMemoryBytes { get; set; }
    public long PostGcMemoryDeltaBytes { get; set; }
}

public sealed class ThumbnailFastGenerationBenchmarkComparison
{
    public bool LegacyMeasured { get; set; }
    public int SampleCount { get; set; }
    public int SeekSettleDelayMilliseconds { get; set; }
    public double? LegacyTotalDurationMs { get; set; }
    public double? FastTotalDurationMs { get; set; }
    public double? SpeedupRatio { get; set; }
    public string? Reason { get; set; }

    public static double? CalculateSpeedupRatio(double? legacyTotalDurationMs, double? fastTotalDurationMs)
    {
        if (legacyTotalDurationMs is null || fastTotalDurationMs is null || fastTotalDurationMs <= 0)
        {
            return null;
        }

        return legacyTotalDurationMs.Value / fastTotalDurationMs.Value;
    }
}

public sealed class ThumbnailFastGenerationBenchmarkResult
{
    public string ProjectPath { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; set; }
    public string BenchmarkDirectory { get; set; } = string.Empty;
    public string? BenchmarkFilePath { get; set; }
    public string? SummaryFilePath { get; set; }
    public string? OverallFailureReason { get; set; }
    public List<ThumbnailFastGenerationBenchmarkRunResult> Runs { get; set; } = [];
    public ThumbnailFastGenerationBenchmarkSummary Summary { get; set; } = new();
    public ThumbnailFastGenerationBenchmarkComparison LegacyComparison { get; set; } = new();
    public List<string> GeneratedFiles { get; set; } = [];
}

public sealed class ThumbnailFastGenerationBenchmarkSummaryReport
{
    public string ProjectPath { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; set; }
    public string BenchmarkDirectory { get; set; } = string.Empty;
    public List<int> SampleCounts { get; set; } = [];
    public List<int> SeekSettleDelayMilliseconds { get; set; } = [];
    public int RunCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public int RequestedFrameCount { get; set; }
    public int CapturedFrameCount { get; set; }
    public int FailedFrameCount { get; set; }
    public int RetryCount { get; set; }
    public double TotalDurationMs { get; set; }
    public double AverageSeekDurationMs { get; set; }
    public double AverageSettleDurationMs { get; set; }
    public double AverageCaptureDurationMs { get; set; }
    public double AverageSaveDurationMs { get; set; }
    public double? FramesPerSecondEffective { get; set; }
    public double? LegacyTotalDurationMs { get; set; }
    public double? SpeedupRatio { get; set; }
    public string? OverallFailureReason { get; set; }
    public long InitialTotalMemoryBytes { get; set; }
    public long FinalTotalMemoryBytes { get; set; }
    public long MemoryDeltaBytes { get; set; }
    public long PostGcTotalMemoryBytes { get; set; }
    public long PostGcMemoryDeltaBytes { get; set; }
}
