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

/// <summary>
/// YMM のプレビューコピー機能を UI Automation 経由で呼び出し、クリップボード画像からサムネイルを生成します。
/// </summary>
//CSJ,class=FastClipboardThumbnailGenerator,高速クリップボードサムネイル生成器
public sealed class FastClipboardThumbnailGenerator
{
    //CSJ,field=ThumbnailCount,サムネイル件数
    private const int ThumbnailCount = 64;
    //CSJ,field=SeekDelayMs,フレーム移動待機時間ミリ秒
    private const int SeekDelayMs = 50;
    private const int PollDelayMs = 50;
    private const int PollMaxCount = 20;
    //CSJ,field=MaxAttempts,最大試行回数
    private const int MaxAttempts = 3;
    private const int NudgeDelayMs = 50;
    private const int NudgeRetryMax = 3;

    //CSJ,field=logger,ロガー
    private readonly FileLogger logger;
    private readonly ThumbnailSequenceFrameRenderer renderer = new();
    private readonly UiaThumbnailCopyInvoker uiaInvoker;

    public FastClipboardThumbnailGenerator(FileLogger logger)
    {
        this.logger = logger;
        uiaInvoker = new UiaThumbnailCopyInvoker(logger);
    }

    //CSJ,method=GoToFrameAsync,指定フレームに移動する（非同期）
    public async Task<bool> GoToFrameAsync(Timeline timeline, int frameIndex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        //CSJ,variable=frame,フレーム
        var frame = Math.Max(0, frameIndex);
        // Timeline は UI スレッド所有のため、CurrentFrame の更新は Dispatcher 経由にする。
        await InvokeOnUiAsync(() => SetTimelineCurrentFrame(timeline, frame)).ConfigureAwait(true);
        await YieldDispatcherAsync(DispatcherPriority.Render).ConfigureAwait(true);
        logger.Info($"GoToFrame set={frame}");
        logger.Flush();
        return true;
    }

    //CSJ,method=CopyPreviewAsync,プレビューをコピーする（非同期）
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

        //CSJ,variable=image,画像
        var image = await PollClipboardImageAsync(cancellationToken).ConfigureAwait(true);
        if (image is null)
        {
            logger.Info("CopyPreview failed. clipboard empty");
            logger.Flush();
            return null;
        }

        //CSJ,variable=signature,シグネチャ
        var signature = ComputePngSha256Hex(image);
        logger.Info($"CopyPreview ok. size={image.PixelWidth}x{image.PixelHeight} hash={signature}");
        logger.Flush();
        return new ClipboardCopyResult(image, signature, null, false);
    }

    //CSJ,method=GenerateAsync,生成する（非同期）
    public async Task<FastClipboardGenerationResult> GenerateAsync(
        //CSJ,parameter=ymmpPath,YMMPパス
        string ymmpPath,
        //CSJ,parameter=timeline,タイムライン
        Timeline timeline,
        //CSJ,parameter=cancellationToken,キャンセルトークン
        CancellationToken cancellationToken)
    {
        var totalSw = Stopwatch.StartNew();
        //CSJ,variable=operationName,操作名前
        const string operationName = "FastThumbnail";

        //CSJ,variable=hash,ハッシュ
        var hash = FilmstripCacheKeyFactory.TryCreateHash(ymmpPath);
        if (string.IsNullOrWhiteSpace(hash))
        {
            logger.Info($"{operationName}: cache hash unavailable. ymmp={ymmpPath}");
            logger.Flush();
            return new FastClipboardGenerationResult(false, "cache hash unavailable", 0, 0, 0, 0, 0, 0, 0, 0, string.Empty);
        }

        //CSJ,variable=cacheDirectory,キャッシュディレクトリ
        var cacheDirectory = Path.Combine(
            AppDirectories.UserDirectory,
            "plugin",
            "YMMProjectManager",
            "cache",
            "filmstrip",
            hash);
        Directory.CreateDirectory(cacheDirectory);

        //CSJ,variable=lengthFrames,長さフレーム列
        var lengthFrames = Math.Max(1, GetTimelineLengthFrames(timeline));
        //CSJ,variable=sampleFrames,サンプルフレーム列
        var sampleFrames = CreateSampleFrames(lengthFrames);
        logger.Info($"{operationName}: start. ymmp={ymmpPath}, hash={hash}, lengthFrames={lengthFrames}, samples={sampleFrames.Length}");
        logger.Flush();

        // 本生成前に 1 回コピーできるか確認し、UIA/Clipboard が使えない環境を早期に検出する。
        //CSJ,variable=testFrame,テストフレーム
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
        //CSJ,variable=failed,失敗
        var failed = 0;
        //CSJ,variable=timeoutCount,タイムアウト件数
        var timeoutCount = 0;

        for (var i = 0; i < sampleFrames.Length; i++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                //CSJ,variable=frame,フレーム
                var frame = sampleFrames[i];
                //CSJ,variable=outputPath,出力パス
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

                // 前回ハッシュと比較し、プレビューが古い画像のまま返るケースを検出する。
                //CSJ,variable=capture,キャプチャ
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

    //CSJ,method=CaptureSlotAsync,スロットを取得する（非同期）
    private async Task<CaptureSlotResult?> CaptureSlotAsync(
        //CSJ,parameter=timeline,タイムライン
        Timeline timeline,
        //CSJ,parameter=targetFrame,対象フレーム
        int targetFrame,
        //CSJ,parameter=slot,スロット
        int slot,
        string? prevHash,
        //CSJ,parameter=cancellationToken,キャンセルトークン
        CancellationToken cancellationToken)
    {
        //CSJ,variable=attempt,試行
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // YMM 側の描画更新が遅れることがあるため、同じスロットを数回まで再試行する。
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

            //CSJ,variable=changed,変更済み
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

        // プレビュー更新を促すため、隣接フレームへ小さく動かしてから対象フレームへ戻す。
        var nudged = false;
        string? lastNudgeReason = null;
        //CSJ,variable=retry,再試行
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
            // フレーム単位の UIA 操作が使えない場合は、次編集点コマンドを代替として試す。
            //CSJ,variable=fallback,代替
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
        //CSJ,parameter=timeline,タイムライン
        Timeline timeline,
        //CSJ,parameter=targetFrame,対象フレーム
        int targetFrame,
        //CSJ,parameter=cancellationToken,キャンセルトークン
        CancellationToken cancellationToken)
    {
        //CSJ,variable=nextBefore,次フレーム移動前の位置
        var nextBefore = GetTimelineCurrentFrame(timeline);
        //CSJ,variable=nextInvoke,次呼び出し
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
        //CSJ,variable=nextAfter,次変更後
        var nextAfter = GetTimelineCurrentFrame(timeline);
        //CSJ,variable=nextDelta,次差分
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
        //CSJ,parameter=timeline,タイムライン
        Timeline timeline,
        //CSJ,parameter=targetFrame,対象フレーム
        int targetFrame,
        //CSJ,parameter=cancellationToken,キャンセルトークン
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
        //CSJ,variable=delta,差分
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
        //CSJ,variable=image,画像
        var image = await PollClipboardImageAsync(cancellationToken).ConfigureAwait(true);
        if (image is null)
        {
            return new ClipboardCopyResult(null, string.Empty, "clipboard timeout", true);
        }

        return new ClipboardCopyResult(image, ComputePngSha256Hex(image), string.Empty, false);
    }

    private async Task<BitmapSource?> PollClipboardImageAsync(CancellationToken cancellationToken)
    {
        // コピー直後は Clipboard 反映が非同期なので、短い間隔で画像到着を待つ。
        for (var poll = 1; poll <= PollMaxCount; poll++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            //CSJ,variable=image,画像
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

                    //CSJ,variable=clone,クローン
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
            // renderer の入力形式へ合わせてからバックグラウンド保存に渡す。
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            if (!converted.IsFrozen && converted.CanFreeze)
            {
                converted.Freeze();
            }

            source = converted;
        }

        //CSJ,variable=frameBytes,フレームバイト列
        var frameBytes = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(frameBytes, source.PixelWidth * 4, 0);
        renderer.SaveThumbnailPng(frameBytes, source.PixelWidth, source.PixelHeight, outputPath);
    }

    //CSJ,method=CreateSampleFrames,サンプルフレーム列を作成する
    private static int[] CreateSampleFrames(int totalFrames)
    {
        // タイムライン全体を 64 スロットへ均等に割り当てる。
        //CSJ,variable=frames,フレーム列
        var frames = new int[ThumbnailCount];
        //CSJ,variable=max,最大
        var max = Math.Max(0, totalFrames - 1);
        for (var i = 0; i < ThumbnailCount; i++)
        {
            frames[i] = max == 0 ? 0 : (int)Math.Floor(i * (max / (double)(ThumbnailCount - 1)));
        }

        return frames;
    }

    //CSJ,method=GetTimelineLengthFrames,タイムライン長さフレーム列を取得する
    private static int GetTimelineLengthFrames(Timeline timeline)
    {
        var value = timeline.GetType().GetProperty("Length", BindingFlags.Public | BindingFlags.Instance)?.GetValue(timeline);
        return ExtractFrame(value, 1);
    }

    //CSJ,method=GetTimelineCurrentFrame,タイムライン現在のフレームを取得する
    private static int GetTimelineCurrentFrame(Timeline timeline)
    {
        var value = timeline.GetType().GetProperty("CurrentFrame", BindingFlags.Public | BindingFlags.Instance)?.GetValue(timeline);
        return ExtractFrame(value, 0);
    }

    //CSJ,method=SetTimelineCurrentFrame,タイムライン現在のフレームを設定する
    private static void SetTimelineCurrentFrame(Timeline timeline, int frame)
    {
        var prop = timeline.GetType().GetProperty("CurrentFrame", BindingFlags.Public | BindingFlags.Instance);
        if (prop is null || !prop.CanWrite)
        {
            return;
        }

        //CSJ,variable=targetType,対象型
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
            //CSJ,variable=instance,インスタンス
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

    //CSJ,method=ExtractFrame,フレームを抽出する
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
            //CSJ,variable=frameValue,フレーム値
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
        //CSJ,variable=encoder,エンコーダー
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        //CSJ,variable=hash,ハッシュ
        var hash = SHA256.HashData(ms.ToArray());
        return Convert.ToHexString(hash)[..12];
    }

    private static Task<T> InvokeOnUiAsync<T>(Func<T> action)
    {
        //CSJ,variable=dispatcher,ディスパッチャー
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return Task.FromResult(action());
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private static Task InvokeOnUiAsync(Action action)
    {
        //CSJ,variable=dispatcher,ディスパッチャー
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
        //CSJ,variable=dispatcher,ディスパッチャー
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(() => { }, priority).Task;
    }

    //CSJ,class=CaptureSlotResult,キャプチャスロット結果
    private sealed class CaptureSlotResult
    {
        public CaptureSlotResult(BitmapSource? image, string hash, string? failureReason, bool isTimeout)
        {
            Image = image;
            Hash = hash;
            FailureReason = failureReason;
            IsTimeout = isTimeout;
        }

        //CSJ,property=Image,画像
        public BitmapSource? Image { get; set; }
        //CSJ,property=Hash,ハッシュ
        public string Hash { get; }
        //CSJ,property=FailureReason,失敗理由
        public string? FailureReason { get; }
        //CSJ,property=IsTimeout,タイムアウトかどうか
        public bool IsTimeout { get; }
    }
}

//CSJ,record=ClipboardCopyResult,クリップボードコピー結果
public sealed record ClipboardCopyResult(BitmapSource? Image, string Hash, string? FailureReason, bool IsTimeout);

//CSJ,record=FastClipboardGenerationResult,クリップボード経由の高速サムネイル生成結果
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
