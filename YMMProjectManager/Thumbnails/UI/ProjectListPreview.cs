using System.IO;
using System.Windows;
using System.Diagnostics;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Application.Thumbnails;
using YMMProjectManager.Infrastructure.Output;
using YMMProjectManager.Infrastructure.Thumbnails;
using YukkuriMovieMaker.Commons;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectListViewModel
{
    private async Task GenerateThumbnailsFastAsync()
    {
        var project = SelectedProject;
        if (project is null)
        {
            return;
        }

        await ExecuteWithBusyAsync("Generate thumbnails (fast)", async () =>
        {
            // 高速サムネイル生成は、YMM 側で開いているタイムライン情報を前提にする。
            var info = TimelineContextService.Info;
            if (info?.Timeline is null)
            {
                MessageBox.Show("Open a project in YMM first.", "YMM Project Manager", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = await seekPreviewThumbnailGenerator
                .GenerateAsync(project.FullPath, info, CancellationToken.None)
                .ConfigureAwait(true);

            if (!result.Success)
            {
                MessageBox.Show(
                    result.Reason,
                    "YMM Project Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // 生成後はキャッシュパスを再通知し、一覧のスクラブサムネイルを更新する。
            UpdateThumbnailMetadata(project);
        }).ConfigureAwait(true);
    }

    private async Task RunSeekProbeAsync()
    {
        await ExecuteWithBusyAsync("SeekProbe", async () =>
        {
            var timeline = TimelineContextService.Timeline;
            if (timeline is null)
            {
                MessageBox.Show("Open a project in YMM first.", "YMM Project Manager", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var outputDirectory = Path.Combine(Path.GetTempPath(), "YMMProjectManager", "seek-probe");
            Directory.CreateDirectory(outputDirectory);

            // 代表フレームを複数試し、YMM のタイムライン操作が安定しているかを確認する。
            var probeFrames = CreateSeekProbeFrames(timeline);
            var seekAdapter = new YmmTimelineSeekAdapter();
            var resultPaths = new List<string>(probeFrames.Count);
            var failedCount = 0;

            foreach (var frame in probeFrames)
            {
                try
                {
                    var path = await seekAdapter.WriteSeekProbeAsync(timeline, frame, outputDirectory, CancellationToken.None).ConfigureAwait(true);
                    resultPaths.Add(path);

                    var json = await File.ReadAllTextAsync(path).ConfigureAwait(true);
                    var result = System.Text.Json.JsonSerializer.Deserialize<SeekResult>(json);
                    if (result is not null && !result.Success)
                    {
                        failedCount++;
                    }

                    logger.Info($"Seek probe saved. frame={frame}, path={path}");
                }
                catch (Exception ex)
                {
                    failedCount++;
                    logger.Error(ex, $"Seek probe failed unexpectedly. frame={frame}");
                }
            }

            var message = resultPaths.Count == 0
                ? $"Seek probe produced no result files.\nOutput: {outputDirectory}"
                : $"Seek probe completed.\nFrames: {string.Join(", ", probeFrames)}\nSaved: {resultPaths.Count}\nFailed: {failedCount}\nOutput: {outputDirectory}";

            MessageBox.Show(
                message,
                "シーク確認",
                MessageBoxButton.OK,
                failedCount == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }).ConfigureAwait(true);
    }

    private void PrepareThumbnailMetadata(IReadOnlyList<ProjectEntry> entries)
    {
        foreach (var entry in entries)
        {
            UpdateThumbnailMetadata(entry);
        }
    }

    private void UpdateThumbnailMetadata(ProjectEntry entry)
    {
        try
        {
            // キャッシュキーはプロジェクトパス由来なので、移動や別名保存時も再計算する。
            var hash = FilmstripCacheKeyFactory.TryCreateHash(entry.FullPath);
            if (string.IsNullOrWhiteSpace(hash))
            {
                entry.ThumbnailCacheDirectory = null;
                entry.ThumbnailSource = null;
                return;
            }

            var cacheDirectory = Path.Combine(
                AppDirectories.UserDirectory,
                "plugin",
                "YMMProjectManager",
                "cache",
                "filmstrip",
                hash);
            entry.ThumbnailCacheDirectory = null;
            entry.ThumbnailCacheDirectory = cacheDirectory;

            var thumbPath = Path.Combine(cacheDirectory, "000.png");
            if (!File.Exists(thumbPath))
            {
                entry.ThumbnailSource = null;
            }
        }
        catch (Exception ex)
        {
            entry.ThumbnailCacheDirectory = null;
            entry.ThumbnailSource = null;
            logger.Error($"UpdateThumbnailMetadata failed: {entry.FullPath}", ex);
        }
    }

    private static List<int> CreateSeekProbeFrames(object timeline)
    {
        var frames = new List<int> { 0, 10, 100, 500, 1000 };
        var lastNearFrame = TryGetTimelineLastNearFrame(timeline);
        if (lastNearFrame is int candidate && candidate >= 0 && !frames.Contains(candidate))
        {
            frames.Add(candidate);
        }

        return frames;
    }

    private static int? TryGetTimelineLastNearFrame(object timeline)
    {
        var lengthProperty = timeline.GetType().GetProperty("Length");
        var lengthValue = lengthProperty?.GetValue(timeline);
        if (!TryConvertFrameLikeValue(lengthValue, out var lengthFrames))
        {
            return null;
        }

        return Math.Max(0, lengthFrames - 1);
    }

    private static bool TryConvertFrameLikeValue(object? value, out int frame)
    {
        frame = 0;
        if (value is int intValue)
        {
            frame = intValue;
            return true;
        }

        if (value is long longValue)
        {
            frame = longValue > int.MaxValue ? int.MaxValue : (int)longValue;
            return true;
        }

        var frameProperty = value?.GetType().GetProperty("Frame");
        var frameValue = frameProperty?.GetValue(value);
        if (frameValue is int nestedInt)
        {
            frame = nestedInt;
            return true;
        }

        if (frameValue is long nestedLong)
        {
            frame = nestedLong > int.MaxValue ? int.MaxValue : (int)nestedLong;
            return true;
        }

        return false;
    }
}
