namespace YMMProjectManager.Application.Thumbnails;

/// <summary>
/// 高速サムネイル生成の通常実行で使う設定値です。
/// </summary>
//CSJ,class=FastThumbnailOptions,高速サムネイルオプション
public sealed class FastThumbnailOptions
{
    //CSJ,property=IsEnabled,有効かどうか
    public bool IsEnabled { get; set; } = true;
    //CSJ,property=SampleCount,サンプル件数
    public int SampleCount { get; set; } = 64;
    //CSJ,property=MaxRetryCount,最大再試行件数
    public int MaxRetryCount { get; set; } = 3;
    //CSJ,property=SeekSettleDelayMilliseconds,フレーム移動後の安定待ち時間（ミリ秒）
    public int SeekSettleDelayMilliseconds { get; set; } = 50;
    //CSJ,property=FallbackToLegacyOnFailure,失敗時に従来方式へ切替
    public bool FallbackToLegacyOnFailure { get; set; } = true;
    //CSJ,property=PreferredGetBitmapCall,優先するGetBitmapの呼び出し
    public string PreferredGetBitmapCall { get; set; } = "GetBitmap(true)";
}

/// <summary>
/// 旧設定名との互換性を保つための高速サムネイル生成オプションです。
/// </summary>
//CSJ,class=FastThumbnailGenerationOptions,高速サムネイル生成オプション
public sealed class FastThumbnailGenerationOptions
{
    //CSJ,property=Enabled,有効
    public bool Enabled { get; set; }
    //CSJ,property=SampleCount,サンプル件数
    public int SampleCount { get; set; } = 64;
    //CSJ,property=SeekSettleDelayMilliseconds,フレーム移動後の安定待ち時間（ミリ秒）
    public int SeekSettleDelayMilliseconds { get; set; } = 50;
    //CSJ,property=MaxRetryCount,最大再試行件数
    public int MaxRetryCount { get; set; } = 3;
    //CSJ,property=AllowClipboardFallback,クリップボード経由の代替取得を許可
    public bool AllowClipboardFallback { get; set; }
    //CSJ,property=AllowScreenCaptureFallback,画面キャプチャによる代替取得を許可
    public bool AllowScreenCaptureFallback { get; set; }
}

/// <summary>
/// プロジェクト全体から均等にサンプルフレームを選ぶためのヘルパーです。
/// </summary>
//CSJ,class=FastThumbnailFrameSampler,高速サムネイル用フレーム抽出
public static class FastThumbnailFrameSampler
{
    /// <summary>
    /// 先頭と末尾を含めて、指定数のフレームを単調増加になるように作成します。
    /// </summary>
    //CSJ,method=CreateSampleFrames,サンプルフレーム列を作成する
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
        //CSJ,variable=frames,フレーム列
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
//CSJ,class=ThumbnailGenerationDiagnostics,サムネイル生成診断情報
public sealed class ThumbnailGenerationDiagnostics
{
    //CSJ,property=FastThumbnailEnabled,高速サムネイル有効
    public bool FastThumbnailEnabled { get; set; }
    //CSJ,property=TimelineFound,タイムラインを検出したか
    public bool TimelineFound { get; set; }
    //CSJ,property=PreviewViewModelFound,プレビュービューモデルを検出したか
    public bool PreviewViewModelFound { get; set; }
    //CSJ,property=GetBitmapFound,GetBitmapを検出したか
    public bool GetBitmapFound { get; set; }
    //CSJ,property=SampleCount,サンプル件数
    public int SampleCount { get; set; }
    //CSJ,property=CapturedCount,取得済みの件数
    public int CapturedCount { get; set; }
    //CSJ,property=FailedFrameCount,失敗フレーム件数
    public int FailedFrameCount { get; set; }
    //CSJ,property=RetryCount,再試行件数
    public int RetryCount { get; set; }
    //CSJ,property=AverageSeekDuration,平均フレーム移動長さ
    public TimeSpan AverageSeekDuration { get; set; }
    //CSJ,property=AverageCaptureDuration,平均キャプチャ長さ
    public TimeSpan AverageCaptureDuration { get; set; }
    //CSJ,property=TotalDuration,合計長さ
    public TimeSpan TotalDuration { get; set; }
    //CSJ,property=FallbackReason,代替理由
    public string? FallbackReason { get; set; }
    //CSJ,property=Warnings,警告一覧
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// 高速サムネイル生成 1 回分の結果です。
/// </summary>
//CSJ,class=FastThumbnailGenerationResult,高速サムネイル生成結果
public sealed class FastThumbnailGenerationResult
{
    //CSJ,property=Success,成功
    public bool Success { get; set; }
    //CSJ,property=RequestedSampleCount,指定サンプル数
    public int RequestedSampleCount { get; set; }
    //CSJ,property=CapturedCount,取得済みの件数
    public int CapturedCount { get; set; }
    //CSJ,property=Duration,長さ
    public TimeSpan Duration { get; set; }
    //CSJ,property=FallbackReason,代替理由
    public string? FallbackReason { get; set; }
    //CSJ,property=Warnings,警告一覧
    public List<string> Warnings { get; set; } = [];
    //CSJ,property=Diagnostics,診断情報
    public ThumbnailGenerationDiagnostics? Diagnostics { get; set; }
}

/// <summary>
/// 高速生成ベンチマークで試すサンプル数と待機時間の組み合わせです。
/// </summary>
//CSJ,class=ThumbnailFastGenerationBenchmarkOptions,サムネイル高速生成性能計測オプション
public sealed class ThumbnailFastGenerationBenchmarkOptions
{
    //CSJ,property=SampleCounts,サンプル数の一覧
    public List<int> SampleCounts { get; set; } = [16, 32, 64, 128, 256];
    //CSJ,property=SeekSettleDelayMilliseconds,フレーム移動後の安定待ち時間（ミリ秒）
    public List<int> SeekSettleDelayMilliseconds { get; set; } = [0, 25, 50, 100];
    //CSJ,property=MaxRetryCount,最大再試行件数
    public int MaxRetryCount { get; set; } = 3;
    //CSJ,property=PersistAllFrames,全フレームを保存する
    public bool PersistAllFrames { get; set; }
    //CSJ,property=IncludeLegacyComparison,従来方式との比較を含めるか
    public bool IncludeLegacyComparison { get; set; }
    //CSJ,property=PreferredGetBitmapCall,優先するGetBitmapの呼び出し
    public string PreferredGetBitmapCall { get; set; } = "GetBitmap(true)";
}

/// <summary>
/// サンプル数とシーク安定待ち時間の 1 組み合わせ分の計測結果です。
/// </summary>
//CSJ,class=ThumbnailFastGenerationBenchmarkRunResult,サムネイル高速生成性能計測実行結果
public sealed class ThumbnailFastGenerationBenchmarkRunResult
{
    //CSJ,property=ProjectPath,プロジェクトのパス
    public string ProjectPath { get; set; } = string.Empty;
    //CSJ,property=SampleCount,サンプル件数
    public int SampleCount { get; set; }
    //CSJ,property=RequestedFrameCount,指定フレーム数
    public int RequestedFrameCount { get; set; }
    //CSJ,property=CapturedFrameCount,取得済みフレーム件数
    public int CapturedFrameCount { get; set; }
    //CSJ,property=FailedFrameCount,失敗フレーム件数
    public int FailedFrameCount { get; set; }
    //CSJ,property=RetryCount,再試行件数
    public int RetryCount { get; set; }
    //CSJ,property=TotalDurationMs,合計長さミリ秒
    public double TotalDurationMs { get; set; }
    //CSJ,property=AverageSeekDurationMs,平均フレーム移動長さミリ秒
    public double AverageSeekDurationMs { get; set; }
    //CSJ,property=AverageSettleDurationMs,平均安定待ち時間（ミリ秒）
    public double AverageSettleDurationMs { get; set; }
    //CSJ,property=AverageCaptureDurationMs,平均キャプチャ長さミリ秒
    public double AverageCaptureDurationMs { get; set; }
    //CSJ,property=AverageSaveDurationMs,平均保存長さミリ秒
    public double AverageSaveDurationMs { get; set; }
    //CSJ,property=MinCaptureDurationMs,最小キャプチャ長さミリ秒
    public double? MinCaptureDurationMs { get; set; }
    //CSJ,property=MaxCaptureDurationMs,最大キャプチャ長さミリ秒
    public double? MaxCaptureDurationMs { get; set; }
    //CSJ,property=MinSeekDurationMs,最小フレーム移動長さミリ秒
    public double? MinSeekDurationMs { get; set; }
    //CSJ,property=MaxSeekDurationMs,最大フレーム移動長さミリ秒
    public double? MaxSeekDurationMs { get; set; }
    //CSJ,property=MinSettleDurationMs,最小安定待ち時間（ミリ秒）
    public double? MinSettleDurationMs { get; set; }
    //CSJ,property=MaxSettleDurationMs,最大安定待ち時間（ミリ秒）
    public double? MaxSettleDurationMs { get; set; }
    //CSJ,property=MinSaveDurationMs,最小保存長さミリ秒
    public double? MinSaveDurationMs { get; set; }
    //CSJ,property=MaxSaveDurationMs,最大保存長さミリ秒
    public double? MaxSaveDurationMs { get; set; }
    //CSJ,property=SeekMeasurementCount,フレーム移動測定件数
    public int SeekMeasurementCount { get; set; }
    //CSJ,property=SettleMeasurementCount,安定待ち時間の計測回数
    public int SettleMeasurementCount { get; set; }
    //CSJ,property=CaptureMeasurementCount,キャプチャ測定件数
    public int CaptureMeasurementCount { get; set; }
    //CSJ,property=SaveMeasurementCount,保存測定件数
    public int SaveMeasurementCount { get; set; }
    //CSJ,property=FramesPerSecondEffective,実効フレーム毎秒
    public double? FramesPerSecondEffective { get; set; }
    //CSJ,property=FallbackUsed,代替方式を使用したか
    public bool FallbackUsed { get; set; }
    //CSJ,property=FallbackReason,代替理由
    public string? FallbackReason { get; set; }
    //CSJ,property=PreferredGetBitmapCall,優先するGetBitmapの呼び出し
    public string PreferredGetBitmapCall { get; set; } = string.Empty;
    //CSJ,property=BitmapWidth,ビットマップ幅
    public int? BitmapWidth { get; set; }
    //CSJ,property=BitmapHeight,ビットマップ高さ
    public int? BitmapHeight { get; set; }
    //CSJ,property=BitmapPixelFormat,ビットマップ画素形式
    public string? BitmapPixelFormat { get; set; }
    //CSJ,property=SavedFrameCount,保存済みフレーム件数
    public int SavedFrameCount { get; set; }
    //CSJ,property=GeneratedAt,生成日時
    public DateTimeOffset GeneratedAt { get; set; }
    //CSJ,property=OutputDirectory,出力ディレクトリ
    public string OutputDirectory { get; set; } = string.Empty;
    //CSJ,property=Warnings,警告一覧
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// ベンチマーク全体を集計した成功数・時間・メモリ情報です。
/// </summary>
//CSJ,class=ThumbnailFastGenerationBenchmarkSummary,サムネイル高速生成性能計測概要
public sealed class ThumbnailFastGenerationBenchmarkSummary
{
    //CSJ,property=RunCount,実行件数
    public int RunCount { get; set; }
    //CSJ,property=SuccessCount,成功件数
    public int SuccessCount { get; set; }
    //CSJ,property=FailureCount,失敗件数
    public int FailureCount { get; set; }
    //CSJ,property=TotalDurationMs,合計長さミリ秒
    public double TotalDurationMs { get; set; }
    //CSJ,property=LegacyTotalDurationMs,旧形式合計長さミリ秒
    public double? LegacyTotalDurationMs { get; set; }
    //CSJ,property=SpeedupRatio,高速化の倍率
    public double? SpeedupRatio { get; set; }
    //CSJ,property=InitialTotalMemoryBytes,初期合計メモリバイト列
    public long InitialTotalMemoryBytes { get; set; }
    //CSJ,property=FinalTotalMemoryBytes,最終合計メモリバイト列
    public long FinalTotalMemoryBytes { get; set; }
    //CSJ,property=MemoryDeltaBytes,メモリ差分バイト列
    public long MemoryDeltaBytes { get; set; }
    //CSJ,property=PostGcTotalMemoryBytes,GC後のメモリ使用量（バイト）
    public long PostGcTotalMemoryBytes { get; set; }
    //CSJ,property=PostGcMemoryDeltaBytes,GC後のメモリ使用量の差分（バイト）
    public long PostGcMemoryDeltaBytes { get; set; }
}

/// <summary>
/// 従来方式と高速方式の比較結果です。
/// </summary>
//CSJ,class=ThumbnailFastGenerationBenchmarkComparison,サムネイル高速生成性能計測比較
public sealed class ThumbnailFastGenerationBenchmarkComparison
{
    //CSJ,property=LegacyMeasured,従来方式を計測したか
    public bool LegacyMeasured { get; set; }
    //CSJ,property=SampleCount,サンプル件数
    public int SampleCount { get; set; }
    //CSJ,property=SeekSettleDelayMilliseconds,フレーム移動後の安定待ち時間（ミリ秒）
    public int SeekSettleDelayMilliseconds { get; set; }
    //CSJ,property=LegacyTotalDurationMs,旧形式合計長さミリ秒
    public double? LegacyTotalDurationMs { get; set; }
    //CSJ,property=FastTotalDurationMs,高速合計長さミリ秒
    public double? FastTotalDurationMs { get; set; }
    //CSJ,property=SpeedupRatio,高速化の倍率
    public double? SpeedupRatio { get; set; }
    //CSJ,property=Reason,理由
    public string? Reason { get; set; }

    /// <summary>
    /// 従来方式が高速方式の何倍時間を要したかを計算します。
    /// </summary>
    //CSJ,method=CalculateSpeedupRatio,高速化の倍率を計算する
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
//CSJ,class=ThumbnailFastGenerationBenchmarkResult,サムネイル高速生成性能計測結果
public sealed class ThumbnailFastGenerationBenchmarkResult
{
    //CSJ,property=ProjectPath,プロジェクトのパス
    public string ProjectPath { get; set; } = string.Empty;
    //CSJ,property=GeneratedAt,生成日時
    public DateTimeOffset GeneratedAt { get; set; }
    //CSJ,property=BenchmarkDirectory,性能計測ディレクトリ
    public string BenchmarkDirectory { get; set; } = string.Empty;
    //CSJ,property=BenchmarkFilePath,性能計測ファイルパス
    public string? BenchmarkFilePath { get; set; }
    //CSJ,property=SummaryFilePath,概要ファイルパス
    public string? SummaryFilePath { get; set; }
    //CSJ,property=OverallFailureReason,処理全体の失敗理由
    public string? OverallFailureReason { get; set; }
    //CSJ,property=Runs,実行記録一覧
    public List<ThumbnailFastGenerationBenchmarkRunResult> Runs { get; set; } = [];
    //CSJ,property=Summary,概要
    public ThumbnailFastGenerationBenchmarkSummary Summary { get; set; } = new();
    //CSJ,property=LegacyComparison,旧形式比較
    public ThumbnailFastGenerationBenchmarkComparison LegacyComparison { get; set; } = new();
    //CSJ,property=GeneratedFiles,生成ファイル一覧
    public List<string> GeneratedFiles { get; set; } = [];
}

/// <summary>
/// UI やログで見やすいように詳細結果から必要な値だけを集約したレポートです。
/// </summary>
//CSJ,class=ThumbnailFastGenerationBenchmarkSummaryReport,サムネイル高速生成性能計測概要報告
public sealed class ThumbnailFastGenerationBenchmarkSummaryReport
{
    //CSJ,property=ProjectPath,プロジェクトのパス
    public string ProjectPath { get; set; } = string.Empty;
    //CSJ,property=GeneratedAt,生成日時
    public DateTimeOffset GeneratedAt { get; set; }
    //CSJ,property=BenchmarkDirectory,性能計測ディレクトリ
    public string BenchmarkDirectory { get; set; } = string.Empty;
    //CSJ,property=SampleCounts,サンプル数の一覧
    public List<int> SampleCounts { get; set; } = [];
    //CSJ,property=SeekSettleDelayMilliseconds,フレーム移動後の安定待ち時間（ミリ秒）
    public List<int> SeekSettleDelayMilliseconds { get; set; } = [];
    //CSJ,property=RunCount,実行件数
    public int RunCount { get; set; }
    //CSJ,property=SuccessCount,成功件数
    public int SuccessCount { get; set; }
    //CSJ,property=FailureCount,失敗件数
    public int FailureCount { get; set; }
    //CSJ,property=RequestedFrameCount,指定フレーム数
    public int RequestedFrameCount { get; set; }
    //CSJ,property=CapturedFrameCount,取得済みフレーム件数
    public int CapturedFrameCount { get; set; }
    //CSJ,property=FailedFrameCount,失敗フレーム件数
    public int FailedFrameCount { get; set; }
    //CSJ,property=RetryCount,再試行件数
    public int RetryCount { get; set; }
    //CSJ,property=TotalDurationMs,合計長さミリ秒
    public double TotalDurationMs { get; set; }
    //CSJ,property=AverageSeekDurationMs,平均フレーム移動長さミリ秒
    public double AverageSeekDurationMs { get; set; }
    //CSJ,property=AverageSettleDurationMs,平均安定待ち時間（ミリ秒）
    public double AverageSettleDurationMs { get; set; }
    //CSJ,property=AverageCaptureDurationMs,平均キャプチャ長さミリ秒
    public double AverageCaptureDurationMs { get; set; }
    //CSJ,property=AverageSaveDurationMs,平均保存長さミリ秒
    public double AverageSaveDurationMs { get; set; }
    //CSJ,property=FramesPerSecondEffective,実効フレーム毎秒
    public double? FramesPerSecondEffective { get; set; }
    //CSJ,property=LegacyTotalDurationMs,旧形式合計長さミリ秒
    public double? LegacyTotalDurationMs { get; set; }
    //CSJ,property=SpeedupRatio,高速化の倍率
    public double? SpeedupRatio { get; set; }
    //CSJ,property=OverallFailureReason,処理全体の失敗理由
    public string? OverallFailureReason { get; set; }
    //CSJ,property=InitialTotalMemoryBytes,初期合計メモリバイト列
    public long InitialTotalMemoryBytes { get; set; }
    //CSJ,property=FinalTotalMemoryBytes,最終合計メモリバイト列
    public long FinalTotalMemoryBytes { get; set; }
    //CSJ,property=MemoryDeltaBytes,メモリ差分バイト列
    public long MemoryDeltaBytes { get; set; }
    //CSJ,property=PostGcTotalMemoryBytes,GC後のメモリ使用量（バイト）
    public long PostGcTotalMemoryBytes { get; set; }
    //CSJ,property=PostGcMemoryDeltaBytes,GC後のメモリ使用量の差分（バイト）
    public long PostGcMemoryDeltaBytes { get; set; }
}
