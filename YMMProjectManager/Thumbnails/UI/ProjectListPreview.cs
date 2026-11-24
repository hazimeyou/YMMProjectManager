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

//CSJ,class=ProjectListViewModel,プロジェクト一覧の表示モデル
public sealed partial class ProjectListViewModel
{
    private async Task GenerateThumbnailsFastAsync()
    {
        //CSJ,variable=project,プロジェクト
        var project = SelectedProject;
        if (project is null)
        {
            return;
        }

        var projectPath = project.FullPath;

        await ExecuteToolAsync("Generate thumbnails (fast)", async token =>
        {
            // 高速サムネイル生成は、YMM 側で開いているタイムライン情報を前提にする。
            //CSJ,variable=info,情報
            var info = TimelineContextService.Info;
            if (info?.Timeline is null)
            {
                MessageBox.Show("Open a project in YMM first.", "YMM Project Manager", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SeekPreviewThumbnailGenerationResult result;
            try
            {
                result = await seekPreviewThumbnailGenerator
                    .GenerateAsync(projectPath, info, () => ReferenceEquals(SelectedProject, project) &&
                        string.Equals(project.FullPath, projectPath, StringComparison.Ordinal), token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                MessageBox.Show("サムネイル生成を中止しました。", "YMM Project Manager",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

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

    //CSJ,method=RunSeekProbeAsync,フレーム移動調査を実行する（非同期）
    private async Task RunSeekProbeAsync()
    {
        await ExecuteWithBusyAsync("SeekProbe", async () =>
        {
            //CSJ,variable=timeline,タイムライン
            var timeline = TimelineContextService.Timeline;
            if (timeline is null)
            {
                MessageBox.Show("Open a project in YMM first.", "YMM Project Manager", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            //CSJ,variable=outputDirectory,出力ディレクトリ
            var outputDirectory = Path.Combine(Path.GetTempPath(), "YMMProjectManager", "seek-probe");
            Directory.CreateDirectory(outputDirectory);

            // 代表フレームを複数試し、YMM のタイムライン操作が安定しているかを確認する。
            //CSJ,variable=probeFrames,調査フレーム列
            var probeFrames = CreateSeekProbeFrames(timeline);
            //CSJ,variable=seekAdapter,フレーム移動アダプター
            var seekAdapter = new YmmTimelineSeekAdapter();
            //CSJ,variable=resultPaths,結果パス一覧
            var resultPaths = new List<string>(probeFrames.Count);
            //CSJ,variable=failedCount,失敗件数
            var failedCount = 0;

            //CSJ,variable=frame,フレーム
            foreach (var frame in probeFrames)
            {
                try
                {
                    //CSJ,variable=path,パス
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

            //CSJ,variable=message,メッセージ
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

    //CSJ,method=PrepareThumbnailMetadata,サムネイルメタデータを準備する
    private void PrepareThumbnailMetadata(IReadOnlyList<ProjectEntry> entries)
    {
        foreach (var entry in entries)
        {
            UpdateThumbnailMetadata(entry);
        }
    }

    //CSJ,method=UpdateThumbnailMetadata,サムネイルメタデータを更新する
    private void UpdateThumbnailMetadata(ProjectEntry entry)
    {
        try
        {
            //CSJ,variable=cacheDirectory,キャッシュディレクトリ
            var cacheDirectory = Path.Combine(
                AppDirectories.UserDirectory,
                "plugin",
                "YMMProjectManager",
                "cache",
                "filmstrip");
            var displayDirectory = ThumbnailCachePublication.GetDisplayDirectory(cacheDirectory, entry.FullPath);
            entry.ThumbnailCacheDirectory = null;
            entry.ThumbnailCacheDirectory = displayDirectory;

            if (displayDirectory is null || !File.Exists(Path.Combine(displayDirectory, "000.png")))
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

    //CSJ,method=CreateSeekProbeFrames,フレーム移動調査フレーム列を作成する
    private static List<int> CreateSeekProbeFrames(object timeline)
    {
        //CSJ,variable=frames,フレーム列
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
        //CSJ,variable=lengthProperty,長さプロパティ
        var lengthProperty = timeline.GetType().GetProperty("Length");
        //CSJ,variable=lengthValue,長さ値
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

        //CSJ,variable=frameProperty,フレームプロパティ
        var frameProperty = value?.GetType().GetProperty("Frame");
        //CSJ,variable=frameValue,フレーム値
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
