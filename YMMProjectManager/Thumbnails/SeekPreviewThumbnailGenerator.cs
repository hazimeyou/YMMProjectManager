using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YMMProjectManager.Infrastructure;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;

namespace YMMProjectManager.Infrastructure.Output;

/// <summary>
/// タイムラインを代表フレームへシークし、現在のプレビュー画像を保存するサムネイル生成器です。
/// </summary>
//CSJ,class=SeekPreviewThumbnailGenerator,フレーム移動プレビューサムネイル生成器
public sealed partial class SeekPreviewThumbnailGenerator
{
    private const int ThumbnailCount = ThumbnailCachePublication.SlotCount;
    private static readonly SemaphoreSlim CaptureGate = new(1);

    private readonly FileLogger logger;

    public SeekPreviewThumbnailGenerator(FileLogger logger, CurrentPreviewCaptureService previewCaptureService)
    {
        this.logger = logger;
        // This constructor remains available to callers of the existing API.
        ArgumentNullException.ThrowIfNull(previewCaptureService);
    }

    //CSJ,method=GenerateAsync,生成する（非同期）
    public Task<SeekPreviewThumbnailGenerationResult> GenerateAsync(
        string ymmpPath, TimelineToolInfo timelineInfo, CancellationToken cancellationToken) =>
        GenerateAsync(ymmpPath, timelineInfo, () => true, cancellationToken);

    internal async Task<SeekPreviewThumbnailGenerationResult> GenerateAsync(string ymmpPath, TimelineToolInfo timelineInfo,
        Func<bool> isSelectionCurrent, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (global::System.Windows.Application.Current?.Dispatcher is not { } dispatcher)
            return Failed("YMM のプレビューを確認できません。");

        async Task<SeekPreviewThumbnailGenerationResult> GenerateOnDispatcherAsync()
        {
            using var source = LiveThumbnailSource.TryBind(Path.GetFullPath(ymmpPath), timelineInfo);
            if (source is null)
                return Failed("選択した PF と YMM のプレビューが同じ対象か確認できません。");
            var cacheRoot = Path.Combine(AppDirectories.UserDirectory, "plugin", "YMMProjectManager", "cache", "filmstrip");
            return await GenerateAsync(ymmpPath, source, isSelectionCurrent, cacheRoot, token).ConfigureAwait(true);
        }

        try
        {
            return dispatcher.CheckAccess()
                ? await GenerateOnDispatcherAsync().ConfigureAwait(true)
                : await dispatcher.InvokeAsync(GenerateOnDispatcherAsync, DispatcherPriority.Normal, token)
                    .Task.Unwrap().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   InvalidOperationException or TargetInvocationException or TypeLoadException)
        {
            logger.Info("Live thumbnail refused by host or storage: " + ex.GetType().Name);
            return Failed("YMM の対象を確認できないか、保存先にアクセスできません。");
        }
    }

    internal async Task<SeekPreviewThumbnailGenerationResult> GenerateAsync(string ymmpPath, ILiveThumbnailSource source,
        Func<bool> isSelectionCurrent, string cacheRoot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!CaptureGate.Wait(0))
            return Failed("別のサムネイル生成が実行中です。");

        var timer = Stopwatch.StartNew();
        try
        {
            if (!source.IsCurrent() || !isSelectionCurrent() || source.Length <= 0)
                return Failed("選択した PF の静止プレビューを確認できません。");

            var path = Path.GetFullPath(ymmpPath);
            using var parent = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(path)!, enforceShare: true);
            using var file = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename: false);
            if (!source.MatchesLoadedProject(file))
                return Failed("YMM が読み込んだ時点の PF と、現在のファイルが一致するか確認できません。既存のサムネイルを使用します。");
            var cacheKey = FilmstripCacheKeyFactory.CreateLiveHash(path, file);

            var length = source.Length;
            var originalFrame = source.Frame;
            var originalPosition = source.Position;
            var original = (Frame: originalFrame, Position: originalPosition);
            var confirmed = original;
            var requested = original;
            if (originalFrame < 0 || originalFrame >= length || originalPosition < TimeSpan.Zero)
                return Failed("元のフレームを確認できません。");

            using var cache = new ThumbnailCachePublication(Path.Combine(cacheRoot, cacheKey));
            var renderer = new ThumbnailSequenceFrameRenderer();
            try
            {
                for (var slot = 0; slot < ThumbnailCount; slot++)
                {
                    RequireCurrent(confirmed.Frame, confirmed.Position);
                    var frame = SampleFrame(slot, length);
                    var position = source.TimeAtFrame(frame);
                    if ((frame, position) != confirmed)
                    {
                        // Track the requested position even if YMM faults after moving there.
                        requested = (frame, position);
                        await source.SeekAsync(position).ConfigureAwait(true);
                    }

                    if (global::System.Windows.Application.Current?.Dispatcher is { } dispatcher && dispatcher.CheckAccess())
                        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background, token).Task.ConfigureAwait(true);

                    RequireCurrent(frame, position);
                    confirmed = (frame, position);
                    var bitmap = source.Capture();
                    RequireCurrent(frame, position);
                    cache.WriteFrame(slot, output => SaveScaledThumbnail(renderer, bitmap, output));
                }
            }
            finally
            {
                // A different project, playback or a manually changed position belongs to the host.
                // On cancellation, still restore our own last position while it can be verified.
                if (source.IsCurrent())
                {
                    var current = (source.Frame, source.Position);
                    if (current != original && (current == confirmed || current == requested))
                        await source.SeekAsync(originalPosition).ConfigureAwait(true);
                }
            }

            // One non-overwriting directory publication follows the final check on the UI thread.
            var cacheDirectory = cache.Publish(token, () =>
            {
                RequireCurrent(originalFrame, originalPosition);
                if (!source.MatchesLoadedProject(file) ||
                    !string.Equals(FilmstripCacheKeyFactory.CreateLiveHash(path, file), cacheKey, StringComparison.Ordinal))
                    throw new InvalidOperationException("生成中に PF が変更されました。");
            });
            return new SeekPreviewThumbnailGenerationResult(true, string.Empty,
                ThumbnailCount, 0, ThumbnailCount, length, timer.ElapsedMilliseconds, cacheDirectory);

            void RequireCurrent(int frame, TimeSpan position)
            {
                token.ThrowIfCancellationRequested();
                if (!isSelectionCurrent() || !source.IsCurrent() || source.Length != length ||
                    source.Frame != frame || source.Position != position)
                    throw new InvalidOperationException("生成中にプロジェクトまたはプレビューの対象が変わりました。");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   InvalidOperationException or OverflowException or TargetInvocationException)
        {
            logger.Info("Live thumbnail not committed: " + ex.GetType().Name);
            return Failed("対象・フレームの照合、または保存に失敗しました。既存のサムネイルを使用します。");
        }
        finally
        {
            CaptureGate.Release();
        }
    }

    private static int SampleFrame(int slot, int length) =>
        (int)((long)slot * (length - 1) / (ThumbnailCount - 1));

    private static SeekPreviewThumbnailGenerationResult Failed(string reason) =>
        new(false, reason, 0, 0, 0, 0, 0, string.Empty);

    private static void SaveScaledThumbnail(ThumbnailSequenceFrameRenderer renderer, BitmapSource bitmap, Stream output)
    {
        BitmapSource source = bitmap.Format == PixelFormats.Bgra32
            ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = checked(source.PixelWidth * 4);
        var frameBytes = new byte[checked(stride * source.PixelHeight)];
        source.CopyPixels(frameBytes, stride, 0);
        renderer.SaveThumbnailPng(frameBytes, source.PixelWidth, source.PixelHeight, output);
    }
}

//CSJ,record=SeekPreviewThumbnailGenerationResult,フレーム移動プレビューサムネイル生成結果
public sealed record SeekPreviewThumbnailGenerationResult(
    bool Success,
    string Reason,
    int RenderedCount,
    int FailedCount,
    int SlotsTotal,
    int LengthFrames,
    long TotalMilliseconds,
    string CacheDirectory);
