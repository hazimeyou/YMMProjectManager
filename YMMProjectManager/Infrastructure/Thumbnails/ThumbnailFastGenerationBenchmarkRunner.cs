using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMMProjectManager.Application.Thumbnails;
using YMMProjectManager.Infrastructure.Output;
using YukkuriMovieMaker.Commons;

namespace YMMProjectManager.Infrastructure.Thumbnails;

public sealed class ThumbnailFastGenerationBenchmarkRunner
{
    private const string BenchmarkRootFolderName = "thumbnail-fast-generation";
    private readonly FileLogger logger;
    private readonly ITimelineSeekAdapter seekAdapter;
    private readonly IPreviewBitmapCaptureAdapter previewCaptureAdapter;
    private readonly ThumbnailSequenceFrameRenderer renderer = new();

    public ThumbnailFastGenerationBenchmarkRunner(
        FileLogger logger,
        ITimelineSeekAdapter? seekAdapter = null,
        IPreviewBitmapCaptureAdapter? previewCaptureAdapter = null)
    {
        this.logger = logger;
        this.seekAdapter = seekAdapter ?? new YmmTimelineSeekAdapter();
        this.previewCaptureAdapter = previewCaptureAdapter ?? new YmmPreviewBitmapCaptureAdapter();
    }

    public async Task<ThumbnailFastGenerationBenchmarkResult> RunAsync(
        string projectPath,
        object? timeline,
        ThumbnailFastGenerationBenchmarkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedOptions = options ?? new ThumbnailFastGenerationBenchmarkOptions();
        var generatedAt = DateTimeOffset.Now;
        var benchmarkDirectory = Path.Combine(Path.GetTempPath(), "YMMProjectManager", BenchmarkRootFolderName);
        Directory.CreateDirectory(benchmarkDirectory);

        var runs = new List<ThumbnailFastGenerationBenchmarkRunResult>();
        var generatedFiles = new List<string>();
        var initialMemory = GC.GetTotalMemory(false);
        var benchmarkStarted = Stopwatch.StartNew();
        string? overallFailureReason = null;
        var sampleCounts = NormalizePositiveValues(normalizedOptions.SampleCounts, new[] { 16, 32, 64, 128, 256 });
        var delayValues = NormalizeNonNegativeValues(normalizedOptions.SeekSettleDelayMilliseconds, new[] { 0, 25, 50, 100 });

        try
        {
            logger.Info($"Thumbnail benchmark start. projectPath={projectPath}, sampleCounts={string.Join(",", sampleCounts)}, delays={string.Join(",", delayValues)}, output={benchmarkDirectory}");

            foreach (var sampleCount in sampleCounts)
            {
                foreach (var delayMs in delayValues)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var runResult = await RunSingleAsync(
                            projectPath,
                            timeline,
                            benchmarkDirectory,
                            generatedAt,
                            sampleCount,
                            delayMs,
                            normalizedOptions,
                            cancellationToken).ConfigureAwait(true);
                        runs.Add(runResult);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, $"Benchmark run failed. sampleCount={sampleCount}, delayMs={delayMs}");
                        runs.Add(CreateFailedRunResult(projectPath, generatedAt, sampleCount, delayMs, ex.Message));
                    }
                }
            }

            var benchmarkFilePath = Path.Combine(benchmarkDirectory, $"benchmark-{generatedAt:yyyyMMdd-HHmmss}.json");
            var summaryFilePath = Path.Combine(benchmarkDirectory, "benchmark-summary.json");

            var baseSummary = BuildSummary(runs, initialMemory);
            var legacyComparison = BuildLegacyComparison(normalizedOptions, runs);
            var summary = new ThumbnailFastGenerationBenchmarkSummary
            {
                RunCount = baseSummary.RunCount,
                SuccessCount = baseSummary.SuccessCount,
                FailureCount = baseSummary.FailureCount,
                TotalDurationMs = baseSummary.TotalDurationMs,
                LegacyTotalDurationMs = legacyComparison?.LegacyTotalDurationMs,
                SpeedupRatio = legacyComparison?.SpeedupRatio,
                InitialTotalMemoryBytes = baseSummary.InitialTotalMemoryBytes,
                FinalTotalMemoryBytes = baseSummary.FinalTotalMemoryBytes,
                MemoryDeltaBytes = baseSummary.MemoryDeltaBytes,
                PostGcTotalMemoryBytes = baseSummary.PostGcTotalMemoryBytes,
                PostGcMemoryDeltaBytes = baseSummary.PostGcMemoryDeltaBytes,
            };

            var result = new ThumbnailFastGenerationBenchmarkResult
            {
                ProjectPath = projectPath,
                GeneratedAt = generatedAt,
                BenchmarkDirectory = benchmarkDirectory,
                BenchmarkFilePath = benchmarkFilePath,
                SummaryFilePath = summaryFilePath,
                OverallFailureReason = overallFailureReason,
                Runs = runs,
                Summary = summary,
                LegacyComparison = legacyComparison,
                GeneratedFiles = generatedFiles,
            };

            var summaryReport = new ThumbnailFastGenerationBenchmarkSummaryReport
            {
                ProjectPath = projectPath,
                GeneratedAt = generatedAt,
                BenchmarkDirectory = benchmarkDirectory,
                SampleCounts = sampleCounts,
                SeekSettleDelayMilliseconds = delayValues,
                RunCount = summary.RunCount,
                SuccessCount = summary.SuccessCount,
                FailureCount = summary.FailureCount,
                RequestedFrameCount = runs.Sum(x => x.RequestedFrameCount),
                CapturedFrameCount = runs.Sum(x => x.CapturedFrameCount),
                FailedFrameCount = runs.Sum(x => x.FailedFrameCount),
                RetryCount = runs.Sum(x => x.RetryCount),
                TotalDurationMs = summary.TotalDurationMs,
                AverageSeekDurationMs = WeightedAverage(runs, x => x.AverageSeekDurationMs, x => x.SeekMeasurementCount),
                AverageSettleDurationMs = WeightedAverage(runs, x => x.AverageSettleDurationMs, x => x.SettleMeasurementCount),
                AverageCaptureDurationMs = WeightedAverage(runs, x => x.AverageCaptureDurationMs, x => x.CaptureMeasurementCount),
                AverageSaveDurationMs = WeightedAverage(runs, x => x.AverageSaveDurationMs, x => x.SaveMeasurementCount),
                FramesPerSecondEffective = summary.TotalDurationMs > 0 && runs.Sum(x => x.CapturedFrameCount) > 0
                    ? runs.Sum(x => x.CapturedFrameCount) / (summary.TotalDurationMs / 1000d)
                    : null,
                LegacyTotalDurationMs = summary.LegacyTotalDurationMs,
                SpeedupRatio = summary.SpeedupRatio,
                OverallFailureReason = overallFailureReason,
                InitialTotalMemoryBytes = summary.InitialTotalMemoryBytes,
                FinalTotalMemoryBytes = summary.FinalTotalMemoryBytes,
                MemoryDeltaBytes = summary.MemoryDeltaBytes,
                PostGcTotalMemoryBytes = summary.PostGcTotalMemoryBytes,
                PostGcMemoryDeltaBytes = summary.PostGcMemoryDeltaBytes,
            };

            foreach (var run in runs)
            {
                if (string.IsNullOrWhiteSpace(run.OutputDirectory) || !Directory.Exists(run.OutputDirectory))
                {
                    continue;
                }

                generatedFiles.AddRange(Directory.EnumerateFiles(run.OutputDirectory, "*.png", SearchOption.AllDirectories));
            }

            generatedFiles.Add(benchmarkFilePath);
            generatedFiles.Add(summaryFilePath);

            await WriteJsonAsync(benchmarkFilePath, result, cancellationToken).ConfigureAwait(true);
            await WriteJsonAsync(summaryFilePath, summaryReport, cancellationToken).ConfigureAwait(true);

            logger.Info($"Thumbnail benchmark end. runs={runs.Count}, failures={summary.FailureCount}, totalMs={summary.TotalDurationMs}, output={benchmarkFilePath}");
            return result;
        }
        catch (Exception ex)
        {
            overallFailureReason = ex.Message;
            logger.Error(ex, "Thumbnail benchmark failed.");
            return new ThumbnailFastGenerationBenchmarkResult
            {
                ProjectPath = projectPath,
                GeneratedAt = generatedAt,
                BenchmarkDirectory = benchmarkDirectory,
                OverallFailureReason = overallFailureReason,
                Runs = runs,
                Summary = new ThumbnailFastGenerationBenchmarkSummary(),
                GeneratedFiles = generatedFiles,
            };
        }
        finally
        {
            benchmarkStarted.Stop();
            logger.Info($"Thumbnail benchmark wall clock ms={benchmarkStarted.ElapsedMilliseconds}");
        }
    }

    private async Task<ThumbnailFastGenerationBenchmarkRunResult> RunSingleAsync(
        string projectPath,
        object? timeline,
        string benchmarkDirectory,
        DateTimeOffset generatedAt,
        int sampleCount,
        int delayMs,
        ThumbnailFastGenerationBenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        var runSw = Stopwatch.StartNew();
        var seekDurations = new List<double>();
        var settleDurations = new List<double>();
        var captureDurations = new List<double>();
        var saveDurations = new List<double>();
        var warnings = new List<string>();
        var selectedCaptureIndexes = GetSelectedFrameIndexes(sampleCount);
        var runDirectory = Path.Combine(benchmarkDirectory, "capture-frames", $"sample-{sampleCount:D3}", $"delay-{delayMs:D3}");
        Directory.CreateDirectory(runDirectory);

        var lengthFrames = Math.Max(1, GetTimelineLengthFrames(timeline));
        var sampleFrames = FastThumbnailFrameSampler.CreateSampleFrames(sampleCount, 0, lengthFrames - 1);
        var capturedCount = 0;
        var failedCount = 0;
        var retryCount = 0;
        var savedFrameCount = 0;
        var fallbackReason = string.Empty;
        int? bitmapWidth = null;
        int? bitmapHeight = null;
        string? bitmapPixelFormat = null;

        logger.Info($"Benchmark run start. sampleCount={sampleCount}, delayMs={delayMs}, lengthFrames={lengthFrames}, output={runDirectory}");

        for (var i = 0; i < sampleFrames.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targetFrame = sampleFrames[i];
            var frameSucceeded = false;
            PreviewCaptureResult? captureResult = null;

            for (var attempt = 1; attempt <= Math.Max(1, options.MaxRetryCount); attempt++)
            {
                retryCount += attempt > 1 ? 1 : 0;

                var seekResult = await seekAdapter.SeekAsync(timeline, targetFrame, cancellationToken).ConfigureAwait(true);
                seekDurations.Add(seekResult.Duration.TotalMilliseconds);
                if (!seekResult.Success)
                {
                    fallbackReason = seekResult.Reason ?? "seek failed";
                    warnings.Add($"frame {i}: seek failed: {fallbackReason}");
                    if (attempt < Math.Max(1, options.MaxRetryCount))
                    {
                        continue;
                    }

                    break;
                }

                var settleSw = Stopwatch.StartNew();
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(true);
                }

                settleSw.Stop();
                settleDurations.Add(settleSw.Elapsed.TotalMilliseconds);

                var captureSw = Stopwatch.StartNew();
                captureResult = await previewCaptureAdapter.TryCaptureAsync(cancellationToken).ConfigureAwait(true);
                captureSw.Stop();
                captureDurations.Add(captureSw.Elapsed.TotalMilliseconds);

                if (!captureResult.Success || captureResult.Bitmap is null)
                {
                    fallbackReason = captureResult.FailureReason ?? "preview capture failed";
                    warnings.Add($"frame {i}: capture failed: {fallbackReason}");
                    if (attempt < Math.Max(1, options.MaxRetryCount))
                    {
                        continue;
                    }

                    break;
                }

                bitmapWidth ??= captureResult.Bitmap.PixelWidth;
                bitmapHeight ??= captureResult.Bitmap.PixelHeight;
                bitmapPixelFormat ??= captureResult.Bitmap.Format.ToString();

                if (options.PersistAllFrames || selectedCaptureIndexes.Contains(i))
                {
                    var saveSw = Stopwatch.StartNew();
                    var outputPath = Path.Combine(runDirectory, $"frame-{targetFrame:D6}.png");
                    SaveScaledThumbnail(captureResult.Bitmap, outputPath);
                    saveSw.Stop();
                    saveDurations.Add(saveSw.Elapsed.TotalMilliseconds);
                    savedFrameCount++;
                }

                capturedCount++;
                frameSucceeded = true;
                break;
            }

            if (!frameSucceeded)
            {
                failedCount++;
            }
        }

        runSw.Stop();

        var runResult = new ThumbnailFastGenerationBenchmarkRunResult
        {
            ProjectPath = projectPath,
            SampleCount = sampleCount,
            RequestedFrameCount = sampleFrames.Length,
            CapturedFrameCount = capturedCount,
            FailedFrameCount = failedCount,
            RetryCount = retryCount,
            TotalDurationMs = runSw.Elapsed.TotalMilliseconds,
            AverageSeekDurationMs = Average(seekDurations),
            AverageSettleDurationMs = Average(settleDurations),
            AverageCaptureDurationMs = Average(captureDurations),
            AverageSaveDurationMs = Average(saveDurations),
            MinCaptureDurationMs = Minimum(captureDurations),
            MaxCaptureDurationMs = Maximum(captureDurations),
            MinSeekDurationMs = Minimum(seekDurations),
            MaxSeekDurationMs = Maximum(seekDurations),
            MinSettleDurationMs = Minimum(settleDurations),
            MaxSettleDurationMs = Maximum(settleDurations),
            MinSaveDurationMs = Minimum(saveDurations),
            MaxSaveDurationMs = Maximum(saveDurations),
            SeekMeasurementCount = seekDurations.Count,
            SettleMeasurementCount = settleDurations.Count,
            CaptureMeasurementCount = captureDurations.Count,
            SaveMeasurementCount = saveDurations.Count,
            FramesPerSecondEffective = runSw.Elapsed.TotalMilliseconds > 0 && capturedCount > 0
                ? capturedCount / (runSw.Elapsed.TotalMilliseconds / 1000d)
                : null,
            FallbackUsed = failedCount > 0 || !string.IsNullOrWhiteSpace(fallbackReason),
            FallbackReason = string.IsNullOrWhiteSpace(fallbackReason) ? null : fallbackReason,
            PreferredGetBitmapCall = options.PreferredGetBitmapCall,
            BitmapWidth = bitmapWidth,
            BitmapHeight = bitmapHeight,
            BitmapPixelFormat = bitmapPixelFormat,
            SavedFrameCount = savedFrameCount,
            GeneratedAt = generatedAt,
            OutputDirectory = runDirectory,
            Warnings = warnings,
        };

        logger.Info($"Benchmark run end. sampleCount={sampleCount}, delayMs={delayMs}, captured={capturedCount}, failed={failedCount}, retries={retryCount}, totalMs={runResult.TotalDurationMs:F1}, saveMs={runResult.AverageSaveDurationMs:F1}");
        return runResult;
    }

    private static ThumbnailFastGenerationBenchmarkSummary BuildSummary(
        IReadOnlyList<ThumbnailFastGenerationBenchmarkRunResult> runs,
        long initialMemory)
    {
        var successCount = runs.Count(x => x.FailedFrameCount == 0 && x.CapturedFrameCount > 0);
        var failureCount = runs.Count(x => x.FailedFrameCount > 0 || x.CapturedFrameCount == 0);
        var totalDurationMs = runs.Sum(x => x.TotalDurationMs);
        var finalMemory = GC.GetTotalMemory(false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var postGcMemory = GC.GetTotalMemory(true);
        return new ThumbnailFastGenerationBenchmarkSummary
        {
            RunCount = runs.Count,
            SuccessCount = successCount,
            FailureCount = failureCount,
            TotalDurationMs = totalDurationMs,
            LegacyTotalDurationMs = null,
            SpeedupRatio = null,
            InitialTotalMemoryBytes = initialMemory,
            FinalTotalMemoryBytes = finalMemory,
            MemoryDeltaBytes = finalMemory - initialMemory,
            PostGcTotalMemoryBytes = postGcMemory,
            PostGcMemoryDeltaBytes = postGcMemory - initialMemory,
        };
    }

    private static ThumbnailFastGenerationBenchmarkComparison? BuildLegacyComparison(
        ThumbnailFastGenerationBenchmarkOptions options,
        IReadOnlyList<ThumbnailFastGenerationBenchmarkRunResult> runs)
    {
        if (!options.IncludeLegacyComparison)
        {
            return new ThumbnailFastGenerationBenchmarkComparison
            {
                LegacyMeasured = false,
                SampleCount = options.LegacyComparisonSampleCount,
                SeekSettleDelayMilliseconds = options.LegacyComparisonSeekSettleDelayMilliseconds,
                LegacyTotalDurationMs = null,
                FastTotalDurationMs = runs.Where(x => x.SampleCount == options.LegacyComparisonSampleCount)
                    .OrderByDescending(x => x.TotalDurationMs)
                    .Select(x => (long?)x.TotalDurationMs)
                    .FirstOrDefault(),
                SpeedupRatio = null,
                Reason = "Legacy comparison was not requested.",
            };
        }

        return new ThumbnailFastGenerationBenchmarkComparison
        {
            LegacyMeasured = false,
            SampleCount = options.LegacyComparisonSampleCount,
            SeekSettleDelayMilliseconds = options.LegacyComparisonSeekSettleDelayMilliseconds,
            LegacyTotalDurationMs = null,
            FastTotalDurationMs = runs.Where(x => x.SampleCount == options.LegacyComparisonSampleCount)
                .OrderByDescending(x => x.TotalDurationMs)
                .Select(x => (long?)x.TotalDurationMs)
                .FirstOrDefault(),
            SpeedupRatio = null,
            Reason = "Legacy clipboard-based comparison was intentionally skipped.",
        };
    }

    private void SaveScaledThumbnail(BitmapSource bitmap, string outputPath)
    {
        BitmapSource source = bitmap;
        if (source.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            if (!converted.IsFrozen && converted.CanFreeze)
            {
                converted.Freeze();
            }

            source = converted;
        }

        var frameBytes = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(frameBytes, source.PixelWidth * 4, 0);
        renderer.SaveThumbnailPng(frameBytes, source.PixelWidth, source.PixelHeight, outputPath);
    }

    private static int GetTimelineLengthFrames(object? timeline)
    {
        if (timeline is null)
        {
            return 1;
        }

        var value = timeline.GetType().GetProperty("Length")?.GetValue(timeline);
        if (value is int intValue)
        {
            return intValue;
        }

        if (value is long longValue)
        {
            return longValue > int.MaxValue ? int.MaxValue : (int)longValue;
        }

        var frameProperty = value?.GetType().GetProperty("Frame");
        var frameValue = frameProperty?.GetValue(value);
        if (frameValue is int frameInt)
        {
            return frameInt;
        }

        if (frameValue is long frameLong)
        {
            return frameLong > int.MaxValue ? int.MaxValue : (int)frameLong;
        }

        return 1;
    }

    private static IReadOnlyList<int> NormalizePositiveValues(IReadOnlyList<int>? values, IReadOnlyList<int> defaults)
    {
        var source = values is { Count: > 0 } ? values : defaults;
        return source.Where(value => value > 0).Distinct().OrderBy(value => value).ToArray();
    }

    private static IReadOnlyList<int> NormalizeNonNegativeValues(IReadOnlyList<int>? values, IReadOnlyList<int> defaults)
    {
        var source = values is { Count: > 0 } ? values : defaults;
        return source.Where(value => value >= 0).Distinct().OrderBy(value => value).ToArray();
    }

    private static double WeightedAverage<T>(
        IReadOnlyList<T> items,
        Func<T, double> valueSelector,
        Func<T, int> weightSelector)
    {
        var weightedTotal = 0d;
        var totalWeight = 0;

        foreach (var item in items)
        {
            var weight = Math.Max(0, weightSelector(item));
            if (weight == 0)
            {
                continue;
            }

            weightedTotal += valueSelector(item) * weight;
            totalWeight += weight;
        }

        return totalWeight == 0 ? 0d : weightedTotal / totalWeight;
    }

    private static ThumbnailFastGenerationBenchmarkRunResult CreateFailedRunResult(
        string projectPath,
        DateTimeOffset generatedAt,
        int sampleCount,
        int delayMs,
        string failureReason)
    {
        return new ThumbnailFastGenerationBenchmarkRunResult
        {
            ProjectPath = projectPath,
            SampleCount = sampleCount,
            RequestedFrameCount = sampleCount,
            CapturedFrameCount = 0,
            FailedFrameCount = sampleCount,
            RetryCount = 0,
            TotalDurationMs = 0,
            AverageSeekDurationMs = 0,
            AverageSettleDurationMs = 0,
            AverageCaptureDurationMs = 0,
            AverageSaveDurationMs = 0,
            MinCaptureDurationMs = null,
            MaxCaptureDurationMs = null,
            MinSeekDurationMs = null,
            MaxSeekDurationMs = null,
            MinSettleDurationMs = null,
            MaxSettleDurationMs = null,
            MinSaveDurationMs = null,
            MaxSaveDurationMs = null,
            SeekMeasurementCount = 0,
            SettleMeasurementCount = 0,
            CaptureMeasurementCount = 0,
            SaveMeasurementCount = 0,
            FramesPerSecondEffective = null,
            FallbackUsed = true,
            FallbackReason = failureReason,
            PreferredGetBitmapCall = null,
            BitmapWidth = null,
            BitmapHeight = null,
            BitmapPixelFormat = null,
            SavedFrameCount = 0,
            GeneratedAt = generatedAt,
            OutputDirectory = Path.Combine(Path.GetTempPath(), "YMMProjectManager", BenchmarkRootFolderName, "capture-frames", $"sample-{sampleCount:D3}", $"delay-{delayMs:D3}"),
            Warnings = [failureReason],
        };
    }

    private static HashSet<int> GetSelectedFrameIndexes(int sampleCount)
    {
        var indexes = new HashSet<int>();
        if (sampleCount <= 0)
        {
            return indexes;
        }

        indexes.Add(0);
        indexes.Add(Math.Max(0, sampleCount / 2));
        indexes.Add(sampleCount - 1);
        return indexes;
    }

    private static double Average(IReadOnlyList<double> durations)
    {
        if (durations.Count == 0)
        {
            return 0d;
        }

        return durations.Average();
    }

    private static double? Minimum(IReadOnlyList<double> durations)
        => durations.Count == 0 ? null : durations.Min();

    private static double? Maximum(IReadOnlyList<double> durations)
        => durations.Count == 0 ? null : durations.Max();

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, new JsonSerializerOptions { WriteIndented = true }, cancellationToken).ConfigureAwait(false);
    }
}
