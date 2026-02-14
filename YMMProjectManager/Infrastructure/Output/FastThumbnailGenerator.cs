using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using YMMProjectManager.Infrastructure;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

namespace YMMProjectManager.Infrastructure.Output;

public sealed class FastThumbnailGenerator
{
    private const int ThumbnailCount = 64;

    private readonly FileLogger logger;
    private readonly ThumbnailSequenceFrameRenderer renderer = new();

    public FastThumbnailGenerator(FileLogger logger)
    {
        this.logger = logger;
    }

    public async Task<FastThumbnailGenerationResult> GenerateAsync(
        string ymmpPath,
        IEditorInfo editorInfo,
        CancellationToken cancellationToken)
    {
        var totalSw = Stopwatch.StartNew();
        var renderSw = new Stopwatch();
        var encodeSw = new Stopwatch();

        var hash = FilmstripCacheKeyFactory.TryCreateHash(ymmpPath);
        if (string.IsNullOrWhiteSpace(hash))
        {
            throw new InvalidOperationException("Could not compute cache hash.");
        }

        var cacheDirectory = Path.Combine(
            AppDirectories.UserDirectory,
            "plugin",
            "YMMProjectManager",
            "cache",
            "filmstrip",
            hash);
        Directory.CreateDirectory(cacheDirectory);

        var videoInfo = editorInfo.VideoInfo;
        var fps = Math.Max(1, videoInfo.FPS);
        var durationFrames = Math.Max(1, editorInfo.TimelineDuration.Frame);
        var sampleFrames = CreateSampleFrames(durationFrames);

        LogDiag($"Fast thumbnail generation start. ymmp={ymmpPath}, hash={hash}, fps={fps}, durationFrames={durationFrames}, samples={sampleFrames.Length}");

        var renderedCount = 0;
        var reusedCount = 0;
        LogDiag("Fast: entering loop");

        using var source = editorInfo.CreateTimelineVideoSource();
        for (var i = 0; i < sampleFrames.Length; i++)
        {
            if (totalSw.Elapsed > TimeSpan.FromSeconds(30))
            {
                LogDiag("Fast: timeout");
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var outputPath = Path.Combine(cacheDirectory, $"{i:000}.png");
            if (File.Exists(outputPath))
            {
                reusedCount++;
                continue;
            }

            var frame = sampleFrames[i];
            LogDiag($"Fast: slot {i} start (frame={frame})");
            var time = new YukkuriMovieMaker.Player.Video.FrameTime(frame, fps).Time;

            LogDiag($"Fast: slot {i} set CurrentFrame before");
            renderSw.Start();
            source.Update(time, TimelineSourceUsage.Paused);
            LogDiag($"Fast: slot {i} set CurrentFrame after");
            LogDiag($"Fast: slot {i} delay before");
            await Task.Delay(1, cancellationToken).ConfigureAwait(true);
            LogDiag($"Fast: slot {i} delay after");
            LogDiag($"Fast: slot {i} invoke preview-copy command before");
            LogDiag($"Fast: slot {i} invoke preview-copy command after");
            LogDiag($"Fast: slot {i} read clipboard image before");
            var bitmap = source.RenderBitmapSource();
            LogDiag($"Fast: slot {i} read clipboard image after");
            renderSw.Stop();

            LogDiag($"Fast: slot {i} scale to 64x36 before");
            encodeSw.Start();
            SaveScaledThumbnail(bitmap, outputPath);
            encodeSw.Stop();
            LogDiag($"Fast: slot {i} scale to 64x36 after");
            LogDiag($"Fast: slot {i} save png");

            renderedCount++;
            await Task.Yield();
        }

        totalSw.Stop();
        LogDiag(
            $"Fast thumbnail generation end. renderedFrames={renderedCount}, reused={reusedCount}, totalSamples={ThumbnailCount}, renderMs={renderSw.ElapsedMilliseconds}, encodeWriteMs={encodeSw.ElapsedMilliseconds}, totalMs={totalSw.ElapsedMilliseconds}");

        return new FastThumbnailGenerationResult(
            hash,
            cacheDirectory,
            renderedCount,
            reusedCount,
            totalSw.ElapsedMilliseconds,
            renderSw.ElapsedMilliseconds,
            encodeSw.ElapsedMilliseconds);
    }

    private void LogDiag(string message)
    {
        logger.Info(message);
        logger.Flush();
    }

    private void SaveScaledThumbnail(BitmapSource bitmap, string outputPath)
    {
        var source = bitmap.Format == System.Windows.Media.PixelFormats.Bgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);

        var frameBytes = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(frameBytes, source.PixelWidth * 4, 0);
        renderer.SaveThumbnailPng(frameBytes, source.PixelWidth, source.PixelHeight, outputPath);
    }

    private static int[] CreateSampleFrames(int totalFrames)
    {
        var frames = new int[ThumbnailCount];
        var maxFrame = Math.Max(0, totalFrames - 1);
        for (var i = 0; i < ThumbnailCount; i++)
        {
            frames[i] = maxFrame == 0
                ? 0
                : (int)Math.Floor(i * (maxFrame / (double)(ThumbnailCount - 1)));
        }

        return frames;
    }
}

public sealed record FastThumbnailGenerationResult(
    string Hash,
    string CacheDirectory,
    int RenderedCount,
    int ReusedCount,
    long TotalMilliseconds,
    long RenderMilliseconds,
    long EncodeMilliseconds);
