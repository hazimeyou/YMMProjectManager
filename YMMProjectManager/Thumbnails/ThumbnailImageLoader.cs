using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YMMProjectManager.Infrastructure.Output;

/// <summary>
/// サムネイル PNG を WPF で表示可能な ImageSource として非同期読み込みします。
/// </summary>
//CSJ,class=ThumbnailImageLoader,サムネイル画像読み込み
public static class ThumbnailImageLoader
{
    //CSJ,field=Cache,キャッシュ
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    //CSJ,method=LoadAsync,読み込む（非同期）
    public static Task<ImageSource?> LoadAsync(string path, FileLogger logger)
    {
        //CSJ,variable=fullPath,完全パス
        var fullPath = Path.GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            Cache.TryRemove(fullPath, out _);
            return Task.FromResult<ImageSource?>(null);
        }

        var lastWriteUtc = File.GetLastWriteTimeUtc(fullPath);
        if (Cache.TryGetValue(fullPath, out var cached) && cached.LastWriteUtc == lastWriteUtc)
        {
            // 更新時刻が同じならデコード済み ImageSource を再利用する。
            return cached.Loader.Value;
        }

        // 同じファイルへの同時読み込みは Lazy<Task> で 1 回にまとめる。
        var entry = new CacheEntry(lastWriteUtc, new Lazy<Task<ImageSource?>>(() => LoadCoreAsync(fullPath, logger)));
        Cache[fullPath] = entry;
        return entry.Loader.Value;
    }

    //CSJ,method=LoadCoreAsync,中核を読み込む（非同期）
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
                //CSJ,variable=stream,ストリーム
                using var stream = File.OpenRead(path);
                //CSJ,variable=image,画像
                var image = new BitmapImage();
                image.BeginInit();
                // OnLoad にしてストリームを閉じても UI 表示できるようにする。
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

    //CSJ,record=CacheEntry,キャッシュ項目
    private sealed record CacheEntry(DateTime LastWriteUtc, Lazy<Task<ImageSource?>> Loader);
}
