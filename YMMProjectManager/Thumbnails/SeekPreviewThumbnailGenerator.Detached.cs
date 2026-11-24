using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Infrastructure.Output;

/// <summary>A source built from separately verified PF bytes, with no access to the editor's seek controls.</summary>
internal interface IDetachedThumbnailSource : IAsyncDisposable
{
    ProjectFileIdentity ProjectVersion { get; }
    int Length { get; }
    bool IsCurrent();
    ValueTask<DetachedThumbnailFrame> CaptureAsync(int frame, CancellationToken token);
}

// ObservedFrame must describe the pixels returned by the source. An adapter must not echo the request.
internal sealed record DetachedThumbnailFrame(int ObservedFrame, BitmapSource Image);

public sealed partial class SeekPreviewThumbnailGenerator
{
    private const int MaxDetachedProjectBytes = 64 * 1024 * 1024;
    private const int MaxDetachedFrameBytes = 32 * 1024 * 1024;

    internal async Task<SeekPreviewThumbnailGenerationResult> GenerateDetachedAsync(string ymmpPath,
        Func<CancellationToken, ValueTask<IDetachedThumbnailSource>> openSource,
        Func<bool> isSelectionCurrent, string cacheRoot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!CaptureGate.Wait(0))
            return Failed("別のサムネイル生成が実行中です。");

        var timer = Stopwatch.StartNew();
        try
        {
            var path = Path.GetFullPath(ymmpPath);
            using var parent = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(path)!, enforceShare: true);
            using var handle = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename: false);
            await using var projectFile = new FileStream(handle, FileAccess.Read);
            if (projectFile.Length > MaxDetachedProjectBytes)
                return Failed("PF がサムネイル生成の読取上限を超えています。");
            var expected = await ProjectFileEvidence.ReadAsync(projectFile, token);
            RequireSelection();

            var cacheKey = FilmstripCacheKeyFactory.CreateDetachedHash(path, expected);
            using var cache = new ThumbnailCachePublication(Path.Combine(cacheRoot, cacheKey));
            var renderedCount = 0;
            int length;
            await using (var source = await openSource(token))
            {
                length = source.Length;
                if (length <= 0)
                    return Failed("描画するフレームがありません。");
                var renderer = new ThumbnailSequenceFrameRenderer();
                DetachedThumbnailFrame? captured = null;

                for (var slot = 0; slot < ThumbnailCount; slot++)
                {
                    RequireCurrent();
                    var frame = SampleFrame(slot, length);
                    if (captured?.ObservedFrame != frame)
                    {
                        captured = await source.CaptureAsync(frame, token);
                        RequireCurrent();
                        if (captured.ObservedFrame != frame || !captured.Image.IsFrozen ||
                            captured.Image.PixelWidth <= 0 || captured.Image.PixelHeight <= 0 ||
                            (long)captured.Image.PixelWidth * captured.Image.PixelHeight > MaxDetachedFrameBytes / 4)
                            throw new InvalidDataException("取得したフレームを確認できません。");
                        renderedCount++;
                    }

                    cache.WriteFrame(slot, output => SaveScaledThumbnail(renderer, captured.Image, output));
                }

                RequireCurrent();

                void RequireCurrent()
                {
                    RequireSelection();
                    if (!source.IsCurrent() || source.Length != length || source.ProjectVersion != expected)
                        throw new InvalidOperationException("生成中に PF または描画対象が変更されました。");
                }
            }

            // Disposal can await host work. Check the selection again before publishing the frozen results.
            var published = cache.Publish(token, RequireSelection);
            return new SeekPreviewThumbnailGenerationResult(true, string.Empty, renderedCount, 0, ThumbnailCount,
                length, timer.ElapsedMilliseconds, published);

            void RequireSelection()
            {
                token.ThrowIfCancellationRequested();
                if (!isSelectionCurrent())
                    throw new InvalidOperationException("生成中に選択した PF が変わりました。");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   InvalidOperationException or InvalidDataException or OverflowException or TargetInvocationException)
        {
            logger.Info("Detached thumbnail not committed: " + ex.GetType().Name);
            return Failed("描画対象または保存先を確認できません。既存のサムネイルを使用します。");
        }
        finally
        {
            CaptureGate.Release();
        }
    }
}
