using System.Reflection;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure.Output;
using YukkuriMovieMaker.Plugin;

namespace YMMProjectManager.Infrastructure.Checkpoint;

public sealed class ThumbnailGenerationService
{
    private const int PreviewSettleDelayMs = 60;

    private readonly FileLogger logger;
    private readonly CurrentPreviewCaptureService previewCaptureService;
    private readonly ThumbnailIntervalPlanner planner;
    private readonly ThumbnailSequenceFrameRenderer renderer = new();

    public ThumbnailGenerationService(FileLogger logger, CurrentPreviewCaptureService previewCaptureService, ThumbnailIntervalPlanner planner)
    {
        this.logger = logger;
        this.previewCaptureService = previewCaptureService;
        this.planner = planner;
    }

    public async Task<CheckpointThumbnailGenerationResult> GenerateAsync(
        string outputDirectory,
        CheckpointThumbnailSettings settings,
        TimelineToolInfo timelineInfo,
        CancellationToken cancellationToken = default)
    {
        var timeline = timelineInfo.Timeline;
        if (timeline is null)
        {
            return new CheckpointThumbnailGenerationResult { Success = false, ErrorMessage = "開いているタイムラインが見つかりません。" };
        }

        if (!TryGetTotalFrames(timeline, out var totalFrames))
        {
            return new CheckpointThumbnailGenerationResult { Success = false, ErrorMessage = "タイムライン長を取得できませんでした。" };
        }

        var fps = TryGetFramesPerSecond(timeline, out var resolvedFps) ? resolvedFps : 30;
        var plan = planner.CreatePlan(settings, totalFrames, fps);
        Directory.CreateDirectory(outputDirectory);

        var seek = new TimeLineSeek(timelineInfo);
        var paths = new List<string>();
        for (var i = 0; i < plan.Frames.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var frame = plan.Frames[i];
            seek.SeekToFrame(frame);
            await YieldDispatcherAsync().ConfigureAwait(true);
            await Task.Delay(PreviewSettleDelayMs, cancellationToken).ConfigureAwait(true);

            var capture = await previewCaptureService.CaptureCurrentPreviewBitmapAsync(cancellationToken).ConfigureAwait(true);
            if (!capture.Success || capture.Bitmap is null)
            {
                logger.Info($"Checkpoint thumbnail capture failed. frame={frame}, reason={capture.FailureReason ?? "unknown"}");
                continue;
            }

            var path = Path.Combine(outputDirectory, $"{i:000}.png");
            SaveScaledThumbnail(capture.Bitmap, path);
            paths.Add(path);
        }

        if (paths.Count == 0)
        {
            return new CheckpointThumbnailGenerationResult { Success = false, ErrorMessage = "サムネイルを1件も生成できませんでした。", ModeLabel = plan.ModeLabel, CustomValue = plan.CustomValue };
        }

        return new CheckpointThumbnailGenerationResult
        {
            Success = true,
            RepresentativeThumbnailPath = paths[0],
            ThumbnailPaths = paths,
            ModeLabel = plan.ModeLabel,
            CustomValue = plan.CustomValue,
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

    private static bool TryGetTotalFrames(object timeline, out int totalFrames)
    {
        totalFrames = 0;
        foreach (var propertyName in new[] { "Length", "TotalFrame", "FrameCount", "EndFrame" })
        {
            if (!TryReadIntLikeProperty(timeline, propertyName, out var value))
            {
                continue;
            }

            totalFrames = propertyName.Equals("EndFrame", StringComparison.OrdinalIgnoreCase)
                ? Math.Max(1, value + 1)
                : Math.Max(1, value);
            return true;
        }

        return false;
    }

    private static bool TryGetFramesPerSecond(object timeline, out int fps)
    {
        fps = 30;
        foreach (var target in new[] { timeline, timeline.GetType().GetProperty("VideoInfo", BindingFlags.Public | BindingFlags.Instance)?.GetValue(timeline) })
        {
            if (target is null)
            {
                continue;
            }

            foreach (var propertyName in new[] { "Fps", "FPS", "FrameRate" })
            {
                var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (property is null)
                {
                    continue;
                }

                var raw = property.GetValue(target);
                switch (raw)
                {
                    case int intValue when intValue > 0:
                        fps = intValue;
                        return true;
                    case long longValue when longValue > 0:
                        fps = longValue > int.MaxValue ? int.MaxValue : (int)longValue;
                        return true;
                    case double doubleValue when doubleValue > 0:
                        fps = (int)Math.Round(doubleValue);
                        return true;
                }
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
        return dispatcher is null ? Task.CompletedTask : dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render).Task;
    }
}
