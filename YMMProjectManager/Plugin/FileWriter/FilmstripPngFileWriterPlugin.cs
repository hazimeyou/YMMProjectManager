using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Output;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileWriter;
using YukkuriMovieMaker.Project;

namespace YMMProjectManager.Plugin.FileWriter;

public sealed class FilmstripPngFileWriterPlugin : IVideoFileWriterPlugin
{
    private static readonly FileLogger Logger = CreateLogger();
    private int lastLengthFrames = 64;

    public string Name => "Filmstrip Thumbnails (64x36 x64)";
    public VideoFileWriterOutputPath OutputPathMode => VideoFileWriterOutputPath.File;

    public UIElement GetVideoConfigView(string projectName, VideoInfo videoInfo, int length)
    {
        lastLengthFrames = Math.Max(1, length);
        return new TextBlock
        {
            Text = "64x36 PNG filmstrip preview will be generated.",
            Margin = new Thickness(8),
        };
    }

    public string GetFileExtention() => "png";

    public bool NeedDownloadResources() => false;

    public Task DownloadResources(ProgressMessage progress, CancellationToken token) => Task.CompletedTask;

    public IVideoFileWriter CreateVideoFileWriter(string path, VideoInfo videoInfo)
    {
        try
        {
            Logger.Info("SLOW PATH: IVideoFileWriter pipeline renders full timeline frames in host. Prefer tool-side fast thumbnail generation when possible.");
            var ymmpPath = YmmProjectPathResolver.TryGetCurrentProjectPath();
            var cacheHash = FilmstripCacheKeyFactory.TryCreateHash(ymmpPath);
            var cacheDirectory = cacheHash is null
                ? null
                : Path.Combine(
                    AppDirectories.UserDirectory,
                    "plugin",
                    "YMMProjectManager",
                    "cache",
                    "filmstrip",
                    cacheHash);

            Logger.Info(
                $"Create writer. output={path}, lengthFrames={lastLengthFrames}, ymmp={ymmpPath ?? "<null>"}, cacheDir={(cacheDirectory ?? "<disabled>")}");

            return new FilmstripPngFileWriter(
                outputPath: path,
                videoInfo: videoInfo,
                lengthFrames: lastLengthFrames,
                cacheDirectory: cacheDirectory,
                forceRegenerate: false,
                logger: Logger);
        }
        catch (Exception ex)
        {
            Logger.Error("CreateVideoFileWriter failed", ex);
            return new FilmstripPngFileWriter(path, videoInfo, 64, null, false, Logger);
        }
    }

    private static FileLogger CreateLogger()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(FilmstripPngFileWriterPlugin).Assembly.Location) ?? AppContext.BaseDirectory;
        var logPath = Path.Combine(assemblyDir, "logs", "YMMProjectManager.log");
        return new FileLogger(logPath);
    }
}
