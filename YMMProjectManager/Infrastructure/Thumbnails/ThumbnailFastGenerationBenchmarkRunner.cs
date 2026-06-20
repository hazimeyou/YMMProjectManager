using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMMProjectManager.Application.Thumbnails;

namespace YMMProjectManager.Infrastructure.Thumbnails;

public sealed class ThumbnailFastGenerationBenchmarkRunner
{
    private readonly FileLogger logger;
    private readonly ITimelineSeekAdapter timelineSeekAdapter;
    private readonly IPreviewBitmapCaptureAdapter previewBitmapCaptureAdapter;

    public ThumbnailFastGenerationBenchmarkRunner(
        FileLogger logger,
        ITimelineSeekAdapter timelineSeekAdapter,
        IPreviewBitmapCaptureAdapter previewBitmapCaptureAdapter)
    {
        this.logger = logger;
        this.timelineSeekAdapter = timelineSeekAdapter;
        this.previewBitmapCaptureAdapter = previewBitmapCaptureAdapter;
    }

    public async Task<ThumbnailFastGenerationBenchmarkResult> RunAsync(
        string projectPath,
        object? timeline,
        ThumbnailFastGenerationBenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        var generatedAt = DateTimeOffset.Now;
        var root = Path.Combine(Path.GetTempPath(), "YMMProjectManager", "thumbnail-fast-generation", generatedAt.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(root);

        var result = new ThumbnailFastGenerationBenchmarkResult
        {
            ProjectPath = projectPath,
            GeneratedAt = generatedAt,
            BenchmarkDirectory = root,
        };

        var initialMemory = GC.GetTotalMemory(false);
        foreach (var sampleCount in options.SampleCounts)
        {
            foreach (var delay in options.SeekSettleDelayMilliseconds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var run = await RunSingleAsync(projectPath, timeline, options, sampleCount, delay, root, generatedAt, cancellationToken).ConfigureAwait(false);
                result.Runs.Add(run);
                result.GeneratedFiles.AddRange(Directory.EnumerateFiles(run.OutputDirectory, "*", SearchOption.TopDirectoryOnly));
            }
        }

        var finalMemory = GC.GetTotalMemory(false);
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

    private async Task<ThumbnailFastGenerationBenchmarkRunResult> RunSingleAsync(
        string projectPath,
        object? timeline,
        ThumbnailFastGenerationBenchmarkOptions options,
        int sampleCount,
        int settleDelayMs,
        string root,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.Combine(root, "capture-frames", $"sample-{sampleCount:D3}", $"delay-{settleDelayMs:D3}");
        Directory.CreateDirectory(outputDirectory);

        var sw = Stopwatch.StartNew();
        var seekDurations = new List<double>();
        var settleDurations = new List<double>();
        var captureDurations = new List<double>();
        var saveDurations = new List<double>();
        var captured = 0;
        var failed = 0;
        var saved = 0;
        int? width = null;
        int? height = null;
        string? pixelFormat = null;

        var frames = FastThumbnailFrameSampler.CreateSampleFrames(sampleCount, 0, Math.Max(0, sampleCount - 1));
        for (var i = 0; i < frames.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var seekSw = Stopwatch.StartNew();
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

    private static ThumbnailFastGenerationBenchmarkSummaryReport CreateSummaryReport(
        string projectPath,
        DateTimeOffset generatedAt,
        string root,
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

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

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
