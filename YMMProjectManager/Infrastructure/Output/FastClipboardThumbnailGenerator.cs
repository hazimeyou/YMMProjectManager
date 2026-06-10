using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YMMProjectManager.Infrastructure;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Project;

namespace YMMProjectManager.Infrastructure.Output;

public sealed class FastClipboardThumbnailGenerator
{
    private const int ThumbnailCount = 64;
    private const int SeekDelayMs = 50;
    private const int PollDelayMs = 50;
    private const int PollMaxCount = 20;
    private const int MaxAttempts = 3;
    private const int NudgeDelayMs = 50;
    private const int NudgeRetryMax = 3;

    private readonly FileLogger logger;
    private readonly ThumbnailSequenceFrameRenderer renderer = new();
    private readonly UiaThumbnailCopyInvoker uiaInvoker;

    public FastClipboardThumbnailGenerator(FileLogger logger)
    {
        this.logger = logger;
        uiaInvoker = new UiaThumbnailCopyInvoker(logger);
    }

    public async Task<bool> GoToFrameAsync(Timeline timeline, int frameIndex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frame = Math.Max(0, frameIndex);
        await InvokeOnUiAsync(() => SetTimelineCurrentFrame(timeline, frame)).ConfigureAwait(true);
        await YieldDispatcherAsync(DispatcherPriority.Render).ConfigureAwait(true);
        logger.Info($"GoToFrame set={frame}");
        logger.Flush();
        return true;
    }

    public async Task<ClipboardCopyResult?> CopyPreviewAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await InvokeOnUiAsync(() =>
        {
            try
            {
                Clipboard.Clear();
            }
            catch (COMException)
            {
            }
        }).ConfigureAwait(true);

        var invoked = await InvokeOnUiAsync(() => uiaInvoker.InvokeCopyThumbnailToClipboard()).ConfigureAwait(true);
        if (!invoked)
        {
            logger.Info("CopyPreview failed. UIA invoke failed.");
            logger.Flush();
            return null;
        }

        var image = await PollClipboardImageAsync(cancellationToken).ConfigureAwait(true);
        if (image is null)
        {
            logger.Info("CopyPreview failed. clipboard empty");
            logger.Flush();
            return null;
        }

        var signature = ComputePngSha256Hex(image);
        logger.Info($"CopyPreview ok. size={image.PixelWidth}x{image.PixelHeight} hash={signature}");
        logger.Flush();
        return new ClipboardCopyResult(image, signature, null, false);
    }

    public async Task<FastClipboardGenerationResult> GenerateAsync(
        string ymmpPath,
        Timeline timeline,
        CancellationToken cancellationToken)
    {
        var totalSw = Stopwatch.StartNew();
        const string operationName = "FastThumbnail";

        var hash = FilmstripCacheKeyFactory.TryCreateHash(ymmpPath);
        if (string.IsNullOrWhiteSpace(hash))
        {
            logger.Info($"{operationName}: cache hash unavailable. ymmp={ymmpPath}");
            logger.Flush();
            return new FastClipboardGenerationResult(false, "cache hash unavailable", 0, 0, 0, 0, 0, 0, 0, 0, string.Empty);
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
        var sampleFrames = CreateSampleFrames(lengthFrames);
        logger.Info($"{operationName}: start. ymmp={ymmpPath}, hash={hash}, lengthFrames={lengthFrames}, samples={sampleFrames.Length}");
        logger.Flush();

        var testFrame = GetTimelineCurrentFrame(timeline);
        var singleShot = await CaptureSlotAsync(timeline, testFrame, -1, null, cancellationToken).ConfigureAwait(true);
        if (singleShot is null)
        {
            logger.Info($"{operationName}: clipboard capture unavailable. testFrame={testFrame}");
            logger.Flush();
            return new FastClipboardGenerationResult(false, "clipboard capture unavailable", 0, 0, 0, 1, sampleFrames.Length, lengthFrames, testFrame, totalSw.ElapsedMilliseconds, cacheDirectory);
        }

        singleShot.Image = null;
        string? prevHash = null;
        var rendered = 0;
        var reused = 0;
        var failed = 0;
        var timeoutCount = 0;

        for (var i = 0; i < sampleFrames.Length; i++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = sampleFrames[i];
                var outputPath = Path.Combine(cacheDirectory, $"{i:000}.png");
                logger.Info($"{operationName}: slot={i} start frame={frame}");
                logger.Flush();

                if (File.Exists(outputPath))
                {
                    reused++;
                    logger.Info($"{operationName}: slot={i} reused path={outputPath}");
                    logger.Flush();
                    continue;
                }

                var capture = await CaptureSlotAsync(timeline, frame, i, prevHash, cancellationToken).ConfigureAwait(true);
                if (capture?.Image is null)
                {
                    failed++;
                    if (capture?.IsTimeout == true)
                    {
                        timeoutCount++;
                    }

                    logger.Info($"{operationName}: slot={i} failed reason={capture?.FailureReason ?? "preview not updated"}");
                    logger.Flush();
                    continue;
                }

                var frozen = capture.Image;
                await Task.Run(() => SaveScaledThumbnail(frozen, outputPath), cancellationToken).ConfigureAwait(false);
                capture.Image = null;
                prevHash = capture.Hash;
                rendered++;

                logger.Info($"{operationName}: slot={i} saved path={outputPath}");
                logger.Flush();

                if (i % 8 == 0)
                {
                    logger.Info($"{operationName}: memory bytes={GC.GetTotalMemory(false)} slot={i}");
                    logger.Flush();
                }
            }
            catch (OperationCanceledException)
            {
                logger.Info($"{operationName}: canceled during slot={i}");
                logger.Flush();
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                logger.Error(ex, $"{operationName}: slot={i} exception");
                logger.Flush();
            }
        }

        totalSw.Stop();
        logger.Info($"{operationName}: end. slotsTotal={sampleFrames.Length}, rendered={rendered}, reused={reused}, failed={failed}, timeoutCount={timeoutCount}, totalMs={totalSw.ElapsedMilliseconds}");
        logger.Flush();

        return new FastClipboardGenerationResult(
            true,
            string.Empty,
            rendered,
            reused,
            failed,
            timeoutCount,
            sampleFrames.Length,
            lengthFrames,
            testFrame,
            totalSw.ElapsedMilliseconds,
            cacheDirectory);
    }

    private async Task<CaptureSlotResult?> CaptureSlotAsync(
        Timeline timeline,
        int targetFrame,
        int slot,
        string? prevHash,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var copied = await SeekCopyAndPollAsync(timeline, targetFrame, cancellationToken).ConfigureAwait(true);
            if (copied is null)
            {
                logger.Info($"slot {slot} frame={targetFrame} attempt={attempt} copy failed");
                logger.Flush();
                continue;
            }
            if (copied.Image is null)
            {
                logger.Info($"slot {slot} frame={targetFrame} attempt={attempt} copy failed reason={copied.FailureReason}");
                logger.Flush();
                if (copied.IsTimeout && attempt == MaxAttempts)
                {
                    return new CaptureSlotResult(null, string.Empty, copied.FailureReason, true);
                }
                continue;
            }

            var changed = prevHash is null || !string.Equals(prevHash, copied.Hash, StringComparison.OrdinalIgnoreCase);
            logger.Info($"slot {slot} hash={copied.Hash} changed={changed} attempt={attempt}");
            logger.Info($"Fast: hash changed={changed}");
            logger.Flush();
            if (changed)
            {
                return new CaptureSlotResult(copied.Image, copied.Hash, null, false);
            }
        }

        return new CaptureSlotResult(null, string.Empty, "preview not updated", false);
    }

    private async Task<ClipboardCopyResult?> SeekCopyAndPollAsync(Timeline timeline, int frame, CancellationToken cancellationToken)
    {
        await GoToFrameAsync(timeline, frame, cancellationToken).ConfigureAwait(true);
        await Task.Delay(SeekDelayMs, cancellationToken).ConfigureAwait(true);

        var nudged = false;
        string? lastNudgeReason = null;
        for (var retry = 1; retry <= NudgeRetryMax; retry++)
        {
            var nudge = await TryNudgeByFrameStepAsync(timeline, frame, cancellationToken).ConfigureAwait(true);
            if (nudge.Success)
            {
                nudged = true;
                break;
            }

            lastNudgeReason = nudge.Reason;
            logger.Info($"Fast: nudge failed reason={nudge.Reason}");
            logger.Flush();
            await InvokeOnUiAsync(() => uiaInvoker.InvalidateNudgeCaches()).ConfigureAwait(true);

            if (!nudge.BothZero)
            {
                break;
            }
        }

        if (!nudged && IsNotFoundOrDisabled(lastNudgeReason))
        {
            var fallback = await TryNudgeByNextEditPointAsync(timeline, frame, cancellationToken).ConfigureAwait(true);
            if (!fallback.Success)
            {
                logger.Info($"Fast: nudge failed reason={fallback.Reason}");
                logger.Flush();
                return null;
            }

            nudged = true;
        }

        if (!nudged)
        {
            return new ClipboardCopyResult(null, string.Empty, "nudge failed", false);
        }

        logger.Info("Fast: nudge invoked (next/prev)");
        logger.Flush();
        await Task.Delay(NudgeDelayMs, cancellationToken).ConfigureAwait(true);
        await GoToFrameAsync(timeline, frame, cancellationToken).ConfigureAwait(true);
        logger.Info("Fast: reset frame back to target");
        logger.Flush();

        await InvokeOnUiAsync(() =>
        {
            try
            {
                Clipboard.Clear();
            }
            catch (COMException)
            {
            }
        }).ConfigureAwait(true);

        var invoked = await InvokeOnUiAsync(() => uiaInvoker.InvokeCopyThumbnailToClipboard()).ConfigureAwait(true);
        if (!invoked)
        {
            return new ClipboardCopyResult(null, string.Empty, "copy invoke failed", false);
        }

        return await PollClipboardImageWithHashAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task<(bool Success, bool BothZero, string Reason)> TryNudgeByFrameStepAsync(
        Timeline timeline,
        int targetFrame,
        CancellationToken cancellationToken)
    {
        var nextBefore = GetTimelineCurrentFrame(timeline);
        var nextInvoke = await InvokeOnUiAsync(() =>
        {
            var ok = uiaInvoker.InvokeNextFrame(out var reason);
            return (ok, reason);
        }).ConfigureAwait(true);
        if (!nextInvoke.ok)
        {
            return (false, false, $"next-frame failed: {nextInvoke.reason}");
        }

        await Task.Delay(50, cancellationToken).ConfigureAwait(true);
        var nextAfter = GetTimelineCurrentFrame(timeline);
        var nextDelta = nextAfter - nextBefore;
        logger.Info($"Fast: nudge next before={nextBefore}, after={nextAfter}, delta={nextDelta}");
        logger.Flush();

        var prevBefore = GetTimelineCurrentFrame(timeline);
        var prevInvoke = await InvokeOnUiAsync(() =>
        {
            var ok = uiaInvoker.InvokePrevFrame(out var reason);
            return (ok, reason);
        }).ConfigureAwait(true);
        if (!prevInvoke.ok)
        {
            return (false, false, $"prev-frame failed: {prevInvoke.reason}");
        }

        await YieldDispatcherAsync(DispatcherPriority.Render).ConfigureAwait(true);
        await Task.Delay(SeekDelayMs, cancellationToken).ConfigureAwait(true);
        var prevAfter = GetTimelineCurrentFrame(timeline);
        var prevDelta = prevAfter - prevBefore;
        logger.Info($"Fast: nudge prev before={prevBefore}, after={prevAfter}, delta={prevDelta}");
        logger.Flush();

        if (nextDelta == 0 && prevDelta == 0)
        {
            return (false, true, "next/prev delta both zero");
        }

        return (true, false, string.Empty);
    }

    private async Task<(bool Success, string Reason)> TryNudgeByNextEditPointAsync(
        Timeline timeline,
        int targetFrame,
        CancellationToken cancellationToken)
    {
        var before = GetTimelineCurrentFrame(timeline);
        var invoked = await InvokeOnUiAsync(() =>
        {
            var ok = uiaInvoker.InvokeNextEditPoint(out var reason);
            return (ok, reason);
        }).ConfigureAwait(true);
        if (!invoked.ok)
        {
            return (false, $"next-edit-point failed: {invoked.reason}");
        }

        var after = GetTimelineCurrentFrame(timeline);
        var delta = after - before;
        logger.Info($"Fast: nudge result before={before}, after={after}, delta={delta}");
        logger.Flush();
        if (Math.Abs(delta) > 5)
        {
            logger.Info("Fast: NextEditPoint jump too large, skip");
            logger.Flush();
            await GoToFrameAsync(timeline, targetFrame, cancellationToken).ConfigureAwait(true);
            return (false, "next-edit-point jump too large");
        }

        return (true, string.Empty);
    }

    private static bool IsNotFoundOrDisabled(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        return reason.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("disabled", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ClipboardCopyResult?> PollClipboardImageWithHashAsync(CancellationToken cancellationToken)
    {
        var image = await PollClipboardImageAsync(cancellationToken).ConfigureAwait(true);
        if (image is null)
        {
            return new ClipboardCopyResult(null, string.Empty, "clipboard timeout", true);
        }

        return new ClipboardCopyResult(image, ComputePngSha256Hex(image), string.Empty, false);
    }

    private async Task<BitmapSource?> PollClipboardImageAsync(CancellationToken cancellationToken)
    {
        for (var poll = 1; poll <= PollMaxCount; poll++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = await InvokeOnUiAsync(() =>
            {
                try
                {
                    if (!Clipboard.ContainsImage())
                    {
                        return null;
                    }

                    var source = Clipboard.GetImage();
                    if (source is null)
                    {
                        return null;
                    }

                    var clone = source.CloneCurrentValue();
                    if (!clone.IsFrozen && clone.CanFreeze)
                    {
                        clone.Freeze();
                    }
                    return clone;
                }
                catch (COMException)
                {
                    return null;
                }
                catch
                {
                    return null;
                }
            }).ConfigureAwait(true);

            if (image is not null)
            {
                return image;
            }

            await Task.Delay(PollDelayMs, cancellationToken).ConfigureAwait(true);
        }

        return null;
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

    private static int GetTimelineLengthFrames(Timeline timeline)
    {
        var value = timeline.GetType().GetProperty("Length", BindingFlags.Public | BindingFlags.Instance)?.GetValue(timeline);
        return ExtractFrame(value, 1);
    }

    private static int GetTimelineCurrentFrame(Timeline timeline)
    {
        var value = timeline.GetType().GetProperty("CurrentFrame", BindingFlags.Public | BindingFlags.Instance)?.GetValue(timeline);
        return ExtractFrame(value, 0);
    }

    private static void SetTimelineCurrentFrame(Timeline timeline, int frame)
    {
        var prop = timeline.GetType().GetProperty("CurrentFrame", BindingFlags.Public | BindingFlags.Instance);
        if (prop is null || !prop.CanWrite)
        {
            return;
        }

        var targetType = prop.PropertyType;
        if (targetType == typeof(int))
        {
            prop.SetValue(timeline, frame);
            return;
        }

        if (targetType == typeof(long))
        {
            prop.SetValue(timeline, (long)frame);
            return;
        }

        var ctor = targetType.GetConstructor([typeof(int)]);
        if (ctor is not null)
        {
            var value = ctor.Invoke([frame]);
            prop.SetValue(timeline, value);
            return;
        }

        var frameProp = targetType.GetProperty("Frame", BindingFlags.Public | BindingFlags.Instance);
        if (frameProp is not null)
        {
            var instance = Activator.CreateInstance(targetType);
            if (instance is not null && frameProp.CanWrite)
            {
                if (frameProp.PropertyType == typeof(int))
                {
                    frameProp.SetValue(instance, frame);
                }
                else if (frameProp.PropertyType == typeof(long))
                {
                    frameProp.SetValue(instance, (long)frame);
                }

                prop.SetValue(timeline, instance);
            }
        }
    }

    private static int ExtractFrame(object? value, int fallback)
    {
        if (value is null)
        {
            return fallback;
        }

        if (value is int i)
        {
            return i;
        }

        if (value is long l)
        {
            return l > int.MaxValue ? int.MaxValue : (int)l;
        }

        var frameProp = value.GetType().GetProperty("Frame", BindingFlags.Public | BindingFlags.Instance);
        if (frameProp is not null)
        {
            var frameValue = frameProp.GetValue(value);
            if (frameValue is int fi)
            {
                return fi;
            }

            if (frameValue is long fl)
            {
                return fl > int.MaxValue ? int.MaxValue : (int)fl;
            }
        }

        return fallback;
    }

    private static string ComputePngSha256Hex(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        var hash = SHA256.HashData(ms.ToArray());
        return Convert.ToHexString(hash)[..12];
    }

    private static Task<T> InvokeOnUiAsync<T>(Func<T> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return Task.FromResult(action());
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private static Task InvokeOnUiAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private static Task YieldDispatcherAsync(DispatcherPriority priority)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(() => { }, priority).Task;
    }

    private sealed class CaptureSlotResult
    {
        public CaptureSlotResult(BitmapSource? image, string hash, string? failureReason, bool isTimeout)
        {
            Image = image;
            Hash = hash;
            FailureReason = failureReason;
            IsTimeout = isTimeout;
        }

        public BitmapSource? Image { get; set; }
        public string Hash { get; }
        public string? FailureReason { get; }
        public bool IsTimeout { get; }
    }
}

public sealed record ClipboardCopyResult(BitmapSource? Image, string Hash, string? FailureReason, bool IsTimeout);

public sealed record FastClipboardGenerationResult(
    bool Success,
    string Reason,
    int RenderedCount,
    int ReusedCount,
    int FailedCount,
    int TimeoutCount,
    int SlotsTotal,
    int LengthFrames,
    int TestFrame,
    long TotalMilliseconds,
    string CacheDirectory);
