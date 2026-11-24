using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace YMMProjectManager.Infrastructure.Output;

internal sealed record ThumbnailGalleryFrame(int Number, BitmapSource Image);

internal static class ThumbnailGalleryCache
{
    private const int MaxEncodedBytes = 1024 * 1024;
    private const int MaxInputDimension = 1024;
    private const int MaxDisplayDimension = 128;

    internal static async Task ReadAsync(string cacheRoot, string projectPath, CancellationToken token,
        Func<IReadOnlyList<ThumbnailGalleryFrame>, string, Task> show)
    {
        var fullPath = Path.GetFullPath(projectPath);
        using var parent = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(fullPath)!, enforceShare: true);
        using var project = new FileStream(OwnedFilePublication.OpenOrdinaryPath(fullPath,
            expectDirectory: false, allowRename: false), FileAccess.Read);
        var (frames, status) = await ReadPinnedAsync(cacheRoot, fullPath, project, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // Keep the reviewed PF and its ancestor paths pinned until the UI has adopted or discarded the results.
        await show(frames, status).ConfigureAwait(false);
    }

    private static async Task<(IReadOnlyList<ThumbnailGalleryFrame>, string)> ReadPinnedAsync(
        string cacheRoot, string fullPath, FileStream project, CancellationToken token)
    {
        if (!Directory.Exists(cacheRoot))
            return ([], "保存済みのサムネイルがありません。");

        var version = project.Length <= SeekPreviewThumbnailGenerator.MaxDetachedProjectBytes
            ? await ProjectFileEvidence.ReadAsync(project, token).ConfigureAwait(false) : null;
        using var root = OwnedFilePublication.DirectoryFence.Open(cacheRoot, enforceShare: true);
        var (path, detached) = ThumbnailCachePublication.FindDisplayDirectory(cacheRoot, fullPath,
            project.SafeFileHandle, root, version, token);
        if (path is null)
            return ([], "保存済みのサムネイルが見つからないか、キャッシュの完了を確認できません。");

        var frames = new List<ThumbnailGalleryFrame>(ThumbnailCachePublication.SlotCount);
        using var cache = OwnedFilePublication.DirectoryFence.Open(path, enforceShare: true);
        if (detached || HasReceipt(path))
        {
            ThumbnailCacheMaintenance.ReadVerifiedFrames(path, MaxEncodedBytes, (slot, image) => frames.Add(new(slot + 1, ReadImage(image))), token);
            return (frames, detached ? $"保存済みサムネイル {frames.Count} 点" :
                $"保存済みサムネイル {frames.Count} 点（PF照合はファイルIDと更新時刻）");
        }

        // Receiptless caches predate the current format. Decode only the numbered files
        // that still exist and describe them accurately as old cache entries.
        var damaged = 0;
        for (var slot = 0; slot < ThumbnailCachePublication.SlotCount; slot++)
        {
            token.ThrowIfCancellationRequested();
            var imagePath = Path.Combine(path, slot.ToString("D3", CultureInfo.InvariantCulture) + ".png");
            if (!File.Exists(imagePath)) continue;
            try
            {
                using var image = OwnedFilePublication.OpenOrdinaryPath(imagePath, expectDirectory: false, allowRename: false);
                frames.Add(new(slot + 1, ReadImage(image)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or NotSupportedException)
            {
                damaged++;
            }
        }
        var status = $"旧キャッシュ（照合情報なし） {frames.Count} 点";
        return (frames, damaged == 0 ? status : $"{status}。{damaged} 点を読み込めません。");
    }

    private static bool HasReceipt(string path)
    {
        try
        {
            _ = File.GetAttributes(Path.Combine(path, "ownership.json"));
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private static BitmapSource ReadImage(SafeFileHandle image)
    {
        var length = RandomAccess.GetLength(image);
        if (length is < 24 or > MaxEncodedBytes)
            throw new InvalidDataException("サムネイル画像のサイズが不正です。");
        var bytes = new byte[(int)length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = RandomAccess.Read(image, bytes.AsSpan(offset), offset);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        var header = bytes.AsSpan();
        var width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
        if (!header[..8].SequenceEqual(signature) || !header[12..16].SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadUInt32BigEndian(header[8..12]) != 13 ||
            width is 0 or > MaxInputDimension || height is 0 or > MaxInputDimension)
            throw new InvalidDataException("サムネイルの PNG ヘッダーまたは寸法が不正です。");

        using var input = new MemoryStream(bytes, writable: false);
        var result = new BitmapImage();
        result.BeginInit();
        result.CacheOption = BitmapCacheOption.OnLoad;
        if (Math.Max(width, height) > MaxDisplayDimension)
        {
            if (width >= height) result.DecodePixelWidth = MaxDisplayDimension;
            else result.DecodePixelHeight = MaxDisplayDimension;
        }
        result.StreamSource = input;
        result.EndInit();
        if (result.PixelWidth is <= 0 or > MaxDisplayDimension || result.PixelHeight is <= 0 or > MaxDisplayDimension)
            throw new InvalidDataException("表示用サムネイルの寸法が不正です。");

        BitmapSource decoded = result.Format == PixelFormats.Bgra32
            ? result : new FormatConvertedBitmap(result, PixelFormats.Bgra32, null, 0);
        var stride = decoded.PixelWidth * 4;
        var pixels = new byte[stride * decoded.PixelHeight];
        decoded.CopyPixels(pixels, stride, 0);
        var snapshot = BitmapSource.Create(decoded.PixelWidth, decoded.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        snapshot.Freeze();
        return snapshot;
    }
}
