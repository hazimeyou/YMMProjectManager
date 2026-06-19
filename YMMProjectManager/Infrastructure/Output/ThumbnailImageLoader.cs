using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YMMProjectManager.Infrastructure.Output;

public static class ThumbnailImageLoader
{
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Task<ImageSource?> LoadAsync(string path, FileLogger logger)
    {
        var fullPath = Path.GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            Cache.TryRemove(fullPath, out _);
            return Task.FromResult<ImageSource?>(null);
        }

        var lastWriteUtc = File.GetLastWriteTimeUtc(fullPath);
        if (Cache.TryGetValue(fullPath, out var cached) && cached.LastWriteUtc == lastWriteUtc)
        {
            return cached.Loader.Value;
        }

        var entry = new CacheEntry(lastWriteUtc, new Lazy<Task<ImageSource?>>(() => LoadCoreAsync(fullPath, logger)));
        Cache[fullPath] = entry;
        return entry.Loader.Value;
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

    private sealed record CacheEntry(DateTime LastWriteUtc, Lazy<Task<ImageSource?>> Loader);
}
