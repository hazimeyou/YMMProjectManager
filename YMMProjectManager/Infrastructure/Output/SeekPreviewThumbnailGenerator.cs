using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMMProjectManager.Infrastructure;
using YMMProjectManager;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;

namespace YMMProjectManager.Infrastructure.Output;

public sealed class SeekPreviewThumbnailGenerator
{
    private const int ThumbnailCount = 64;
    private const int PreviewSettleDelayMs = 60;

    private readonly FileLogger logger;
    private readonly CurrentPreviewCaptureService previewCaptureService;
    private readonly ThumbnailSequenceFrameRenderer renderer = new();

    public SeekPreviewThumbnailGenerator(FileLogger logger, CurrentPreviewCaptureService previewCaptureService)
    {
        this.logger = logger;
        this.previewCaptureService = previewCaptureService;
    }

    public async Task<SeekPreviewThumbnailGenerationResult> GenerateAsync(
        string ymmpPath,
        TimelineToolInfo timelineInfo,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var timeline = timelineInfo.Timeline;
        if (timeline is null)
        {
            return new SeekPreviewThumbnailGenerationResult(false, "Timeline not found.", 0, 0, 0, 0, 0, string.Empty);
        }

        var hash = FilmstripCacheKeyFactory.TryCreateHash(ymmpPath);
        if (string.IsNullOrWhiteSpace(hash))
        {
            return new SeekPreviewThumbnailGenerationResult(false, "cache hash unavailable", 0, 0, 0, 0, 0, string.Empty);
        }

        if (!TryGetLastFrame(timeline, out var lastFrame, out var failureReason))
        {
            return new SeekPreviewThumbnailGenerationResult(false, failureReason, 0, 0, 0, 0, 0, string.Empty);
        }

        var totalFrames = Math.Max(1, lastFrame + 1);
        var sampleFrames = CreateSampleFrames(totalFrames);

        var cacheDirectory = Path.Combine(
            AppDirectories.UserDirectory,
            "plugin",
            "YMMProjectManager",
            "cache",
            "filmstrip",
            hash);
        Directory.CreateDirectory(cacheDirectory);
        var renderedCount = 0;
        var failedCount = 0;
        var seek = new TimeLineSeek(timelineInfo);
        logger.Info($"Seek preview thumbnail generation start. ymmp={ymmpPath}, hash={hash}, lastFrame={lastFrame}, samples={sampleFrames.Length}");
        logger.Flush();

        for (var i = 0; i < sampleFrames.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var frame = sampleFrames[i];
            var outputPath = Path.Combine(cacheDirectory, $"{i:000}.png");
            logger.Info($"Seek preview thumbnail slot={i} start frame={frame}");
            logger.Flush();

            seek.SeekToFrame(frame);
            await YieldDispatcherAsync().ConfigureAwait(true);
            await Task.Delay(PreviewSettleDelayMs, cancellationToken).ConfigureAwait(true);

            var capture = await previewCaptureService.CaptureCurrentPreviewBitmapAsync(cancellationToken).ConfigureAwait(true);
            if (!capture.Success || capture.Bitmap is null)
            {
                failedCount++;
                logger.Info($"Seek preview thumbnail slot={i} capture failed reason={capture.FailureReason ?? "unknown"}");
                logger.Flush();
                continue;
            }

            SaveScaledThumbnail(capture.Bitmap, outputPath);
            renderedCount++;

            logger.Info($"Seek preview thumbnail slot={i} saved path={outputPath}");
            logger.Flush();
        }

        sw.Stop();
        var success = renderedCount > 0 && failedCount == 0;
        logger.Info($"Seek preview thumbnail generation end. rendered={renderedCount}, failed={failedCount}, totalMs={sw.ElapsedMilliseconds}");
        logger.Flush();

        return new SeekPreviewThumbnailGenerationResult(
            success,
            success ? string.Empty : "One or more thumbnails failed.",
            renderedCount,
            failedCount,
            sampleFrames.Length,
            totalFrames,
            sw.ElapsedMilliseconds,
            cacheDirectory);
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

    private static int[] CreateSampleFrames(int totalFrames)
    {
        var frames = new int[ThumbnailCount];
        var max = Math.Max(0, totalFrames - 1);
        for (var i = 0; i < ThumbnailCount; i++)
        {
            frames[i] = max == 0 ? 0 : (int)Math.Floor(i * (max / (double)(ThumbnailCount - 1)));
        }

        return frames;
    }

    private static bool TryGetLastFrame(object timeline, out int lastFrame, out string failureReason)
    {
        lastFrame = 0;
        failureReason = "No usable timeline length candidate was found.";

        foreach (var propertyName in new[] { "Length", "TotalFrame", "FrameCount", "EndFrame" })
        {
            if (!TryReadIntLikeProperty(timeline, propertyName, out var value))
            {
                continue;
            }

            if (!propertyName.Equals("EndFrame", StringComparison.OrdinalIgnoreCase) && value <= 0)
            {
                continue;
            }

            lastFrame = propertyName.Equals("EndFrame", StringComparison.OrdinalIgnoreCase)
                ? Math.Max(0, value)
                : Math.Max(0, value - 1);
            failureReason = string.Empty;
            return true;
        }

        var videoInfo = timeline.GetType().GetProperty("VideoInfo", BindingFlags.Public | BindingFlags.Instance)?.GetValue(timeline);
        if (videoInfo is not null)
        {
            foreach (var propertyName in new[] { "Length", "TotalFrame", "FrameCount" })
            {
                if (!TryReadIntLikeProperty(videoInfo, propertyName, out var value) || value <= 0)
                {
                    continue;
                }

                lastFrame = Math.Max(0, value - 1);
                failureReason = string.Empty;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadIntLikeProperty(object instance, string propertyName, out int value)
    {
        value = 0;
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property is null)
        {
            return false;
        }

        var raw = property.GetValue(instance);
        if (raw is int i)
        {
            value = i;
            return true;
        }

        if (raw is long l)
        {
            value = l > int.MaxValue ? int.MaxValue : (int)l;
            return true;
        }

        return false;
    }

    private static Task YieldDispatcherAsync()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render).Task;
    }
}

public sealed record SeekPreviewThumbnailGenerationResult(
    bool Success,
    string Reason,
    int RenderedCount,
    int FailedCount,
    int SlotsTotal,
    int LengthFrames,
    long TotalMilliseconds,
    string CacheDirectory);
