namespace YMMProjectManager.Application.Thumbnails;

/// <summary>
/// 高速サムネイル生成の通常実行で使う設定値です。
/// </summary>
public sealed class FastThumbnailOptions
{
    public bool IsEnabled { get; set; } = true;
    public int SampleCount { get; set; } = 64;
    public int MaxRetryCount { get; set; } = 3;
    public int SeekSettleDelayMilliseconds { get; set; } = 50;
    public bool FallbackToLegacyOnFailure { get; set; } = true;
    public string PreferredGetBitmapCall { get; set; } = "GetBitmap(true)";
}

/// <summary>
/// 旧設定名との互換性を保つための高速サムネイル生成オプションです。
/// </summary>
public sealed class FastThumbnailGenerationOptions
{
    public bool Enabled { get; set; }
    public int SampleCount { get; set; } = 64;
    public int SeekSettleDelayMilliseconds { get; set; } = 50;
    public int MaxRetryCount { get; set; } = 3;
    public bool AllowClipboardFallback { get; set; }
    public bool AllowScreenCaptureFallback { get; set; }
}

/// <summary>
/// プロジェクト全体から均等にサンプルフレームを選ぶためのヘルパーです。
/// </summary>
public static class FastThumbnailFrameSampler
{
    /// <summary>
    /// 先頭と末尾を含めて、指定数のフレームを単調増加になるように作成します。
    /// </summary>
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

        // Round を使うことで短い範囲でも先頭・末尾を保ちながら均等に散らす。
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

/// <summary>
/// 高速サムネイル生成で観測した探索・取得・フォールバック情報です。
/// </summary>
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

/// <summary>
/// 高速サムネイル生成 1 回分の結果です。
/// </summary>
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

/// <summary>
/// 高速生成ベンチマークで試すサンプル数と待機時間の組み合わせです。
/// </summary>
public sealed class ThumbnailFastGenerationBenchmarkOptions
{
    public List<int> SampleCounts { get; set; } = [16, 32, 64, 128, 256];
    public List<int> SeekSettleDelayMilliseconds { get; set; } = [0, 25, 50, 100];
    public int MaxRetryCount { get; set; } = 3;
    public bool PersistAllFrames { get; set; }
    public bool IncludeLegacyComparison { get; set; }
    public string PreferredGetBitmapCall { get; set; } = "GetBitmap(true)";
}

/// <summary>
/// サンプル数とシーク安定待ち時間の 1 組み合わせ分の計測結果です。
/// </summary>
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

/// <summary>
/// ベンチマーク全体を集計した成功数・時間・メモリ情報です。
/// </summary>
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

/// <summary>
/// 従来方式と高速方式の比較結果です。
/// </summary>
public sealed class ThumbnailFastGenerationBenchmarkComparison
{
    public bool LegacyMeasured { get; set; }
    public int SampleCount { get; set; }
    public int SeekSettleDelayMilliseconds { get; set; }
    public double? LegacyTotalDurationMs { get; set; }
    public double? FastTotalDurationMs { get; set; }
    public double? SpeedupRatio { get; set; }
    public string? Reason { get; set; }

    /// <summary>
    /// 従来方式が高速方式の何倍時間を要したかを計算します。
    /// </summary>
    public static double? CalculateSpeedupRatio(double? legacyTotalDurationMs, double? fastTotalDurationMs)
    {
        if (legacyTotalDurationMs is null || fastTotalDurationMs is null || fastTotalDurationMs <= 0)
        {
            return null;
        }

        return legacyTotalDurationMs.Value / fastTotalDurationMs.Value;
    }
}

/// <summary>
/// ベンチマークの詳細結果と出力ファイル一覧です。
/// </summary>
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

/// <summary>
/// UI やログで見やすいように詳細結果から必要な値だけを集約したレポートです。
/// </summary>
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
