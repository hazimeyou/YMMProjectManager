using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YMMProjectManager.Infrastructure.Output;

public static class ThumbnailImageLoader
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<ImageSource?>>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Task<ImageSource?> LoadAsync(string path, FileLogger logger)
    {
        var fullPath = Path.GetFullPath(path);
        var lazy = Cache.GetOrAdd(fullPath, p => new Lazy<Task<ImageSource?>>(() => LoadCoreAsync(p, logger)));
        return lazy.Value;
    }

    private static async Task<ImageSource?> LoadCoreAsync(string path, FileLogger logger)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return (ImageSource?)image;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error($"Thumbnail load failed: {path}", ex);
            return null;
        }
    }
}
