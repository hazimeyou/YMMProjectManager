using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMMProjectManager.Application.Thumbnails;

namespace YMMProjectManager.Infrastructure.Thumbnails;

/// <summary>
/// タイムラインシークとプレビュー取得を組み合わせて、高速サムネイル生成の計測結果を出力します。
/// </summary>
//CSJ,class=ThumbnailFastGenerationBenchmarkRunner,サムネイル高速生成性能計測実行器
public sealed class ThumbnailFastGenerationBenchmarkRunner
{
    //CSJ,field=logger,ロガー
    private readonly FileLogger logger;
    //CSJ,field=timelineSeekAdapter,タイムラインフレーム移動アダプター
    private readonly ITimelineSeekAdapter timelineSeekAdapter;
    //CSJ,field=previewBitmapCaptureAdapter,プレビュービットマップキャプチャアダプター
    private readonly IPreviewBitmapCaptureAdapter previewBitmapCaptureAdapter;

    public ThumbnailFastGenerationBenchmarkRunner(
        //CSJ,parameter=logger,ロガー
        FileLogger logger,
        //CSJ,parameter=timelineSeekAdapter,タイムラインフレーム移動アダプター
        ITimelineSeekAdapter timelineSeekAdapter,
        //CSJ,parameter=previewBitmapCaptureAdapter,プレビュービットマップキャプチャアダプター
        IPreviewBitmapCaptureAdapter previewBitmapCaptureAdapter)
    {
        this.logger = logger;
        this.timelineSeekAdapter = timelineSeekAdapter;
        this.previewBitmapCaptureAdapter = previewBitmapCaptureAdapter;
    }

    //CSJ,method=RunAsync,実行する（非同期）
    public async Task<ThumbnailFastGenerationBenchmarkResult> RunAsync(
        //CSJ,parameter=projectPath,プロジェクトのパス
        string projectPath,
        //CSJ,parameter=timeline,タイムライン
        object? timeline,
        //CSJ,parameter=options,オプション
        ThumbnailFastGenerationBenchmarkOptions options,
        //CSJ,parameter=cancellationToken,キャンセルトークン
        CancellationToken cancellationToken)
    {
        var generatedAt = DateTimeOffset.Now;
        //CSJ,variable=root,ルート
        var root = Path.Combine(Path.GetTempPath(), "YMMProjectManager", "thumbnail-fast-generation", generatedAt.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(root);

        var result = new ThumbnailFastGenerationBenchmarkResult
        {
            ProjectPath = projectPath,
            GeneratedAt = generatedAt,
            BenchmarkDirectory = root,
        };

        // すべての設定組み合わせを同じ出力ルートへ保存し、後から比較しやすくする。
        //CSJ,variable=initialMemory,初期メモリ
        var initialMemory = GC.GetTotalMemory(false);
        //CSJ,variable=sampleCount,サンプル件数
        foreach (var sampleCount in options.SampleCounts)
        {
            //CSJ,variable=delay,待機時間
            foreach (var delay in options.SeekSettleDelayMilliseconds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                //CSJ,variable=run,実行
                var run = await RunSingleAsync(projectPath, timeline, options, sampleCount, delay, root, generatedAt, cancellationToken).ConfigureAwait(false);
                result.Runs.Add(run);
                result.GeneratedFiles.AddRange(Directory.EnumerateFiles(run.OutputDirectory, "*", SearchOption.TopDirectoryOnly));
            }
        }

        //CSJ,variable=finalMemory,最終メモリ
        var finalMemory = GC.GetTotalMemory(false);
        // 実行直後と GC 後の両方を残し、短時間の一時割り当ても確認できるようにする。
        GC.Collect();
        var postGcMemory = GC.GetTotalMemory(true);

        result.Summary = new ThumbnailFastGenerationBenchmarkSummary
        {
            RunCount = result.Runs.Count,
            SuccessCount = result.Runs.Count(x => x.FailedFrameCount == 0),
            FailureCount = result.Runs.Count(x => x.FailedFrameCount > 0),
            TotalDurationMs = result.Runs.Sum(x => x.TotalDurationMs),
            InitialTotalMemoryBytes = initialMemory,
            FinalTotalMemoryBytes = finalMemory,
            MemoryDeltaBytes = finalMemory - initialMemory,
            PostGcTotalMemoryBytes = postGcMemory,
            PostGcMemoryDeltaBytes = postGcMemory - initialMemory,
        };

        result.LegacyComparison = new ThumbnailFastGenerationBenchmarkComparison
        {
            LegacyMeasured = false,
            SampleCount = options.SampleCounts.FirstOrDefault(),
            SeekSettleDelayMilliseconds = options.SeekSettleDelayMilliseconds.FirstOrDefault(),
            Reason = "Legacy comparison was not requested.",
        };

        result.BenchmarkFilePath = Path.Combine(root, "benchmark.json");
        result.SummaryFilePath = Path.Combine(root, "benchmark-summary.json");
        await WriteJsonAsync(result.BenchmarkFilePath, result, cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(result.SummaryFilePath, CreateSummaryReport(projectPath, generatedAt, root, options, result), cancellationToken).ConfigureAwait(false);
        result.GeneratedFiles.Add(result.BenchmarkFilePath);
        result.GeneratedFiles.Add(result.SummaryFilePath);

        logger.Info($"Thumbnail benchmark completed. runs={result.Runs.Count}, output={root}");
        return result;
    }

    //CSJ,method=RunSingleAsync,単一を実行する（非同期）
    private async Task<ThumbnailFastGenerationBenchmarkRunResult> RunSingleAsync(
        //CSJ,parameter=projectPath,プロジェクトのパス
        string projectPath,
        //CSJ,parameter=timeline,タイムライン
        object? timeline,
        //CSJ,parameter=options,オプション
        ThumbnailFastGenerationBenchmarkOptions options,
        //CSJ,parameter=sampleCount,サンプル件数
        int sampleCount,
        int settleDelayMs,
        //CSJ,parameter=root,ルート
        string root,
        DateTimeOffset generatedAt,
        //CSJ,parameter=cancellationToken,キャンセルトークン
        CancellationToken cancellationToken)
    {
        //CSJ,variable=outputDirectory,出力ディレクトリ
        var outputDirectory = Path.Combine(root, "capture-frames", $"sample-{sampleCount:D3}", $"delay-{settleDelayMs:D3}");
        Directory.CreateDirectory(outputDirectory);

        var sw = Stopwatch.StartNew();
        var seekDurations = new List<double>();
        var settleDurations = new List<double>();
        var captureDurations = new List<double>();
        var saveDurations = new List<double>();
        //CSJ,variable=captured,取得済み
        var captured = 0;
        //CSJ,variable=failed,失敗
        var failed = 0;
        //CSJ,variable=saved,保存済み
        var saved = 0;
        //CSJ,variable=width,幅
        int? width = null;
        //CSJ,variable=height,高さ
        int? height = null;
        //CSJ,variable=pixelFormat,画素形式
        string? pixelFormat = null;

        // 実プロジェクト長を直接知らない呼び出しでも計測できるよう、サンプル数分の仮フレームを使う。
        //CSJ,variable=frames,フレーム列
        var frames = FastThumbnailFrameSampler.CreateSampleFrames(sampleCount, 0, Math.Max(0, sampleCount - 1));
        for (var i = 0; i < frames.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var seekSw = Stopwatch.StartNew();
            //CSJ,variable=seek,フレーム移動
            var seek = await timelineSeekAdapter.SeekAsync(timeline, frames[i], cancellationToken).ConfigureAwait(false);
            seekSw.Stop();
            seekDurations.Add(seekSw.Elapsed.TotalMilliseconds);
            if (!seek.Success)
            {
                failed++;
                continue;
            }

            var settleSw = Stopwatch.StartNew();
            if (settleDelayMs > 0)
            {
                await Task.Delay(settleDelayMs, cancellationToken).ConfigureAwait(false);
            }
            settleSw.Stop();
            settleDurations.Add(settleSw.Elapsed.TotalMilliseconds);

            var captureSw = Stopwatch.StartNew();
            //CSJ,variable=capture,キャプチャ
            var capture = await previewBitmapCaptureAdapter.TryCaptureAsync(cancellationToken).ConfigureAwait(false);
            captureSw.Stop();
            captureDurations.Add(captureSw.Elapsed.TotalMilliseconds);
            if (!capture.Success || capture.Bitmap is null)
            {
                failed++;
                continue;
            }

            captured++;
            width ??= capture.Bitmap.PixelWidth;
            height ??= capture.Bitmap.PixelHeight;
            pixelFormat ??= capture.Bitmap.Format.ToString();

            if (options.PersistAllFrames || saved == 0)
            {
                // 全保存しない場合も 1 枚は残し、生成物の視覚確認を可能にする。
                var saveSw = Stopwatch.StartNew();
                SavePng(capture.Bitmap, Path.Combine(outputDirectory, $"{i:000}.png"));
                saveSw.Stop();
                saveDurations.Add(saveSw.Elapsed.TotalMilliseconds);
                saved++;
            }
        }

        sw.Stop();
        return new ThumbnailFastGenerationBenchmarkRunResult
        {
            ProjectPath = projectPath,
            SampleCount = sampleCount,
            RequestedFrameCount = frames.Length,
            CapturedFrameCount = captured,
            FailedFrameCount = failed,
            RetryCount = 0,
            TotalDurationMs = sw.Elapsed.TotalMilliseconds,
            AverageSeekDurationMs = Average(seekDurations),
            AverageSettleDurationMs = Average(settleDurations),
            AverageCaptureDurationMs = Average(captureDurations),
            AverageSaveDurationMs = Average(saveDurations),
            MinCaptureDurationMs = captureDurations.Count == 0 ? null : captureDurations.Min(),
            MaxCaptureDurationMs = captureDurations.Count == 0 ? null : captureDurations.Max(),
            MinSeekDurationMs = seekDurations.Count == 0 ? null : seekDurations.Min(),
            MaxSeekDurationMs = seekDurations.Count == 0 ? null : seekDurations.Max(),
            MinSettleDurationMs = settleDurations.Count == 0 ? null : settleDurations.Min(),
            MaxSettleDurationMs = settleDurations.Count == 0 ? null : settleDurations.Max(),
            MinSaveDurationMs = saveDurations.Count == 0 ? null : saveDurations.Min(),
            MaxSaveDurationMs = saveDurations.Count == 0 ? null : saveDurations.Max(),
            SeekMeasurementCount = seekDurations.Count,
            SettleMeasurementCount = settleDurations.Count,
            CaptureMeasurementCount = captureDurations.Count,
            SaveMeasurementCount = saveDurations.Count,
            FramesPerSecondEffective = sw.Elapsed.TotalSeconds <= 0 ? null : captured / sw.Elapsed.TotalSeconds,
            PreferredGetBitmapCall = options.PreferredGetBitmapCall,
            BitmapWidth = width,
            BitmapHeight = height,
            BitmapPixelFormat = pixelFormat,
            SavedFrameCount = saved,
            GeneratedAt = generatedAt,
            OutputDirectory = outputDirectory,
        };
    }

    //CSJ,method=CreateSummaryReport,概要報告を作成する
    private static ThumbnailFastGenerationBenchmarkSummaryReport CreateSummaryReport(
        //CSJ,parameter=projectPath,プロジェクトのパス
        string projectPath,
        DateTimeOffset generatedAt,
        //CSJ,parameter=root,ルート
        string root,
        //CSJ,parameter=options,オプション
        ThumbnailFastGenerationBenchmarkOptions options,
        ThumbnailFastGenerationBenchmarkResult result)
    {
        return new ThumbnailFastGenerationBenchmarkSummaryReport
        {
            ProjectPath = projectPath,
            GeneratedAt = generatedAt,
            BenchmarkDirectory = root,
            SampleCounts = options.SampleCounts,
            SeekSettleDelayMilliseconds = options.SeekSettleDelayMilliseconds,
            RunCount = result.Summary.RunCount,
            SuccessCount = result.Summary.SuccessCount,
            FailureCount = result.Summary.FailureCount,
            RequestedFrameCount = result.Runs.Sum(x => x.RequestedFrameCount),
            CapturedFrameCount = result.Runs.Sum(x => x.CapturedFrameCount),
            FailedFrameCount = result.Runs.Sum(x => x.FailedFrameCount),
            RetryCount = result.Runs.Sum(x => x.RetryCount),
            TotalDurationMs = result.Summary.TotalDurationMs,
            AverageSeekDurationMs = Average(result.Runs.Select(x => x.AverageSeekDurationMs)),
            AverageSettleDurationMs = Average(result.Runs.Select(x => x.AverageSettleDurationMs)),
            AverageCaptureDurationMs = Average(result.Runs.Select(x => x.AverageCaptureDurationMs)),
            AverageSaveDurationMs = Average(result.Runs.Select(x => x.AverageSaveDurationMs)),
            FramesPerSecondEffective = AverageNullable(result.Runs.Select(x => x.FramesPerSecondEffective)),
            InitialTotalMemoryBytes = result.Summary.InitialTotalMemoryBytes,
            FinalTotalMemoryBytes = result.Summary.FinalTotalMemoryBytes,
            MemoryDeltaBytes = result.Summary.MemoryDeltaBytes,
            PostGcTotalMemoryBytes = result.Summary.PostGcTotalMemoryBytes,
            PostGcMemoryDeltaBytes = result.Summary.PostGcMemoryDeltaBytes,
        };
    }

    //CSJ,method=WriteJsonAsync,JSONを書き込む（非同期）
    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
    }

    //CSJ,method=SavePng,PNGを保存する
    private static void SavePng(BitmapSource bitmap, string path)
    {
        //CSJ,variable=encoder,エンコーダー
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        //CSJ,variable=stream,ストリーム
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    //CSJ,method=Average,平均
    private static double Average(IEnumerable<double> values)
    {
        var array = values.ToArray();
        return array.Length == 0 ? 0 : array.Average();
    }

    private static double? AverageNullable(IEnumerable<double?> values)
    {
        var array = values.Where(x => x is not null).Select(x => x!.Value).ToArray();
        return array.Length == 0 ? null : array.Average();
    }
}
