using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMMProjectManager.Application.Thumbnails;
using YMMProjectManager.Infrastructure.Output;
using YukkuriMovieMaker.Commons;
using ExperimentalFastThumbnailGenerationResult = YMMProjectManager.Application.Thumbnails.FastThumbnailGenerationResult;

namespace YMMProjectManager.Infrastructure.Thumbnails;

public sealed class FastThumbnailGenerationService : IFastThumbnailGenerationService
{
    private readonly FileLogger logger;
    private readonly FastThumbnailGenerationOptions options;
    private readonly YmmTimelineSeekAdapter seekAdapter;
    private readonly YmmPreviewBitmapCaptureAdapter previewCaptureAdapter;
    private readonly ThumbnailSequenceFrameRenderer renderer = new();

    public FastThumbnailGenerationService(
        FileLogger logger,
        FastThumbnailGenerationOptions? options = null,
        YmmTimelineSeekAdapter? seekAdapter = null,
        YmmPreviewBitmapCaptureAdapter? previewCaptureAdapter = null)
    {
        this.logger = logger;
        this.options = options ?? new FastThumbnailGenerationOptions();
        this.seekAdapter = seekAdapter ?? new YmmTimelineSeekAdapter();
        this.previewCaptureAdapter = previewCaptureAdapter ?? new YmmPreviewBitmapCaptureAdapter();
    }

    public async Task<ExperimentalFastThumbnailGenerationResult> GenerateAsync(string ymmpPath, object? timeline, CancellationToken cancellationToken)
    {
        var totalSw = Stopwatch.StartNew();
        var warnings = new List<string>();
        var diagnostics = new ThumbnailGenerationDiagnostics
        {
            FastThumbnailEnabled = options.Enabled,
            TimelineFound = timeline is not null,
            SampleCount = options.SampleCount,
        };

        if (!options.Enabled)
        {
            totalSw.Stop();
            diagnostics = diagnostics with { TotalDuration = totalSw.Elapsed };
            var result = CreateResult(false, 0, 0, "fast mode disabled", warnings, diagnostics, totalSw.Elapsed);
            logger.Info("Fast preview thumbnail generation skipped because feature flag is disabled.");
            return result;
        }

        if (timeline is null)
        {
            totalSw.Stop();
            diagnostics = diagnostics with { TotalDuration = totalSw.Elapsed };
            var result = CreateResult(false, 0, 0, "timeline unavailable", warnings, diagnostics, totalSw.Elapsed);
            logger.Info("Fast preview thumbnail generation failed: timeline unavailable.");
            return result;
        }

        var hash = FilmstripCacheKeyFactory.TryCreateHash(ymmpPath);
        if (string.IsNullOrWhiteSpace(hash))
        {
            totalSw.Stop();
            warnings.Add("cache hash unavailable");
            diagnostics = diagnostics with { TotalDuration = totalSw.Elapsed, Warnings = warnings };
            return CreateResult(false, 0, 0, "cache hash unavailable", warnings, diagnostics, totalSw.Elapsed);
        }

        var cacheDirectory = Path.Combine(
            AppDirectories.UserDirectory,
            "plugin",
            "YMMProjectManager",
            "cache",
            "filmstrip",
            hash);
        Directory.CreateDirectory(cacheDirectory);

        var lengthFrames = Math.Max(1, GetTimelineLengthFrames(timeline));
        var sampleFrames = FastThumbnailFrameSampler.CreateSampleFrames(options.SampleCount, 0, lengthFrames - 1);
        var seekDurations = new List<TimeSpan>(sampleFrames.Length);
        var captureDurations = new List<TimeSpan>(sampleFrames.Length);
        var capturedCount = 0;
        var failedFrames = 0;
        var retryCount = 0;
        var previewViewModelFound = false;
        var getBitmapFound = false;
        var fallbackReason = string.Empty;

        logger.Info($"Fast preview generation start. ymmp={ymmpPath}, sampleCount={sampleFrames.Length}, lengthFrames={lengthFrames}, cacheDirectory={cacheDirectory}");

        for (var i = 0; i < sampleFrames.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = sampleFrames[i];
            var outputPath = Path.Combine(cacheDirectory, $"{i:000}.png");
            PreviewCaptureResult? captureResult = null;
            var frameSucceeded = false;

            for (var attempt = 1; attempt <= Math.Max(1, options.MaxRetryCount); attempt++)
            {
                retryCount += attempt > 1 ? 1 : 0;

                var seekResult = await seekAdapter.SeekAsync(timeline, frame, cancellationToken).ConfigureAwait(true);
                seekDurations.Add(seekResult.Duration);
                if (!seekResult.Success)
                {
                    warnings.Add($"frame {i}: seek failed: {seekResult.Reason ?? "unknown"}");
                    fallbackReason = seekResult.Reason ?? "seek failed";
                    continue;
                }

                if (options.SeekSettleDelayMilliseconds > 0)
                {
                    await Task.Delay(options.SeekSettleDelayMilliseconds, cancellationToken).ConfigureAwait(true);
                }

                var captureSw = Stopwatch.StartNew();
                captureResult = await previewCaptureAdapter.TryCaptureAsync(cancellationToken).ConfigureAwait(true);
                captureSw.Stop();
                captureDurations.Add(captureSw.Elapsed);

                previewViewModelFound = captureResult.PreviewViewModelFound;
                getBitmapFound = captureResult.GetBitmapFound;

                if (!captureResult.Success || captureResult.Bitmap is null)
                {
                    warnings.Add($"frame {i}: capture failed: {captureResult.FailureReason ?? "unknown"}");
                    fallbackReason = captureResult.FailureReason ?? "preview capture failed";
                    if (attempt < options.MaxRetryCount)
                    {
                        continue;
                    }

                    break;
                }

                await Task.Run(() => SaveScaledThumbnail(captureResult.Bitmap, outputPath), cancellationToken).ConfigureAwait(true);
                capturedCount++;
                frameSucceeded = true;
                logger.Info($"Fast preview frame saved. index={i}, frame={frame}, path={outputPath}");
                break;
            }

            if (!frameSucceeded)
            {
                failedFrames++;
            }
        }

        totalSw.Stop();

        diagnostics = diagnostics with
        {
            TimelineFound = true,
            PreviewViewModelFound = previewViewModelFound,
            GetBitmapFound = getBitmapFound,
            SampleCount = sampleFrames.Length,
            CapturedCount = capturedCount,
            FailedFrameCount = failedFrames,
            RetryCount = retryCount,
            AverageSeekDuration = Average(seekDurations),
            AverageCaptureDuration = Average(captureDurations),
            TotalDuration = totalSw.Elapsed,
            FallbackReason = string.IsNullOrWhiteSpace(fallbackReason) ? null : fallbackReason,
            Warnings = warnings,
        };

        var success = capturedCount > 0 && failedFrames == 0;
        if (!success && string.IsNullOrWhiteSpace(fallbackReason))
        {
            fallbackReason = "experimental capture incomplete";
        }

        logger.Info(
            $"Fast preview generation end. success={success}, captured={capturedCount}, failed={failedFrames}, retries={retryCount}, avgSeekMs={diagnostics.AverageSeekDuration.TotalMilliseconds:F1}, avgCaptureMs={diagnostics.AverageCaptureDuration.TotalMilliseconds:F1}, totalMs={diagnostics.TotalDuration.TotalMilliseconds:F1}, fallback={fallbackReason}");

        return CreateResult(success, sampleFrames.Length, capturedCount, fallbackReason, warnings, diagnostics, totalSw.Elapsed);
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

    private static int GetTimelineLengthFrames(object timeline)
    {
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

    private static TimeSpan Average(IReadOnlyList<TimeSpan> durations)
    {
        if (durations.Count == 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromTicks((long)durations.Average(x => x.Ticks));
    }

    private static ExperimentalFastThumbnailGenerationResult CreateResult(
        bool success,
        int requestedSampleCount,
        int capturedCount,
        string? fallbackReason,
        IReadOnlyList<string> warnings,
        ThumbnailGenerationDiagnostics diagnostics,
        TimeSpan duration)
    {
        return new ExperimentalFastThumbnailGenerationResult
        {
            Success = success,
            RequestedSampleCount = requestedSampleCount,
            CapturedCount = capturedCount,
            Duration = duration,
            FallbackReason = string.IsNullOrWhiteSpace(fallbackReason) ? null : fallbackReason,
            Warnings = warnings,
            Diagnostics = diagnostics,
        };
    }
}
