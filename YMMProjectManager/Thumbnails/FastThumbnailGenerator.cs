using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using YMMProjectManager.Infrastructure;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

namespace YMMProjectManager.Infrastructure.Output;

/// <summary>
/// YMM の TimelineVideoSource を直接描画して、フィルムストリップ用のサムネイル PNG 群を生成します。
/// </summary>
//CSJ,class=FastThumbnailGenerator,高速サムネイル生成器
public sealed class FastThumbnailGenerator
{
    //CSJ,field=ThumbnailCount,サムネイル件数
    private const int ThumbnailCount = 64;

    //CSJ,field=logger,ロガー
    private readonly FileLogger logger;
    private readonly ThumbnailSequenceFrameRenderer renderer = new();

    public FastThumbnailGenerator(FileLogger logger)
    {
        this.logger = logger;
    }

    //CSJ,method=GenerateAsync,生成する（非同期）
    public async Task<FastThumbnailGenerationResult> GenerateAsync(
        //CSJ,parameter=ymmpPath,YMMPパス
        string ymmpPath,
        IEditorInfo editorInfo,
        //CSJ,parameter=cancellationToken,キャンセルトークン
        CancellationToken cancellationToken)
    {
        var totalSw = Stopwatch.StartNew();
        var renderSw = new Stopwatch();
        var encodeSw = new Stopwatch();

        //CSJ,variable=hash,ハッシュ
        var hash = FilmstripCacheKeyFactory.TryCreateHash(ymmpPath);
        if (string.IsNullOrWhiteSpace(hash))
        {
            throw new InvalidOperationException("Could not compute cache hash.");
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

        //CSJ,variable=videoInfo,動画情報
        var videoInfo = editorInfo.VideoInfo;
        var fps = Math.Max(1, videoInfo.FPS);
        //CSJ,variable=durationFrames,継続フレーム数
        var durationFrames = Math.Max(1, editorInfo.TimelineDuration.Frame);
        //CSJ,variable=sampleFrames,サンプルフレーム列
        var sampleFrames = CreateSampleFrames(durationFrames);

        LogDiag($"Fast thumbnail generation start. ymmp={ymmpPath}, hash={hash}, fps={fps}, durationFrames={durationFrames}, samples={sampleFrames.Length}");

        // 既存キャッシュは再利用し、未生成スロットだけを描画することで再表示を軽くする。
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

            //CSJ,variable=outputPath,出力パス
            var outputPath = Path.Combine(cacheDirectory, $"{i:000}.png");
            if (File.Exists(outputPath))
            {
                reusedCount++;
                continue;
            }

            //CSJ,variable=frame,フレーム
            var frame = sampleFrames[i];
            LogDiag($"Fast: slot {i} start (frame={frame})");
            // TimelineVideoSource は時間指定なので、フレーム番号を FPS 基準の時刻へ変換する。
            //CSJ,variable=time,時刻
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
            //CSJ,variable=bitmap,ビットマップ
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
        // 後段の renderer は BGRA32 前提なので、入力形式をここで揃える。
        var source = bitmap.Format == System.Windows.Media.PixelFormats.Bgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);

        //CSJ,variable=frameBytes,フレームバイト列
        var frameBytes = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(frameBytes, source.PixelWidth * 4, 0);
        renderer.SaveThumbnailPng(frameBytes, source.PixelWidth, source.PixelHeight, outputPath);
    }

    //CSJ,method=CreateSampleFrames,サンプルフレーム列を作成する
    private static int[] CreateSampleFrames(int totalFrames)
    {
        // 64 枚でプロジェクト全体を均等に代表させ、スクラブ時の位置感を保つ。
        //CSJ,variable=frames,フレーム列
        var frames = new int[ThumbnailCount];
        //CSJ,variable=maxFrame,最大フレーム
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

//CSJ,record=FastThumbnailGenerationResult,高速サムネイル生成結果
public sealed record FastThumbnailGenerationResult(
    string Hash,
    string CacheDirectory,
    int RenderedCount,
    int ReusedCount,
    long TotalMilliseconds,
    long RenderMilliseconds,
    long EncodeMilliseconds);
