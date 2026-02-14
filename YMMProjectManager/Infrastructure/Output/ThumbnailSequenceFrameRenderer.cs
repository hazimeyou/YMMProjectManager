using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YMMProjectManager.Infrastructure.Output;

public sealed class ThumbnailSequenceFrameRenderer
{
    public const int ThumbnailWidth = 64;
    public const int ThumbnailHeight = 36;
    private const int BytesPerPixel = 4;
    private readonly byte[] pixels = new byte[ThumbnailWidth * ThumbnailHeight * BytesPerPixel];

    public void SaveThumbnailPng(byte[] frameBytes, int frameWidth, int frameHeight, string outputPath)
    {
        RenderThumbnailPixels(frameBytes, frameWidth, frameHeight, pixels);
        SavePng(pixels, outputPath);
    }

    public static void SaveBlankPng(string outputPath)
    {
        var pixels = new byte[ThumbnailWidth * ThumbnailHeight * BytesPerPixel];
        SavePng(pixels, outputPath);
    }

    private static void RenderThumbnailPixels(byte[] frameBytes, int frameWidth, int frameHeight, byte[] output)
    {
        Array.Clear(output);
        if (frameWidth <= 0 || frameHeight <= 0)
        {
            return;
        }

        if (frameBytes.Length < frameWidth * frameHeight * BytesPerPixel)
        {
            return;
        }

        var scale = Math.Min(
            ThumbnailWidth / (double)frameWidth,
            ThumbnailHeight / (double)frameHeight);
        var scaledWidth = Math.Max(1, (int)Math.Round(frameWidth * scale));
        var scaledHeight = Math.Max(1, (int)Math.Round(frameHeight * scale));
        var offsetX = (ThumbnailWidth - scaledWidth) / 2;
        var offsetY = (ThumbnailHeight - scaledHeight) / 2;

        for (var y = 0; y < scaledHeight; y++)
        {
            var srcY = scaledHeight == 1
                ? 0
                : (int)Math.Round(y * (frameHeight - 1d) / (scaledHeight - 1d));
            for (var x = 0; x < scaledWidth; x++)
            {
                var srcX = scaledWidth == 1
                    ? 0
                    : (int)Math.Round(x * (frameWidth - 1d) / (scaledWidth - 1d));

                var srcIndex = ((srcY * frameWidth) + srcX) * BytesPerPixel;
                var dstIndex = (((offsetY + y) * ThumbnailWidth) + (offsetX + x)) * BytesPerPixel;

                output[dstIndex + 0] = frameBytes[srcIndex + 0]; // B
                output[dstIndex + 1] = frameBytes[srcIndex + 1]; // G
                output[dstIndex + 2] = frameBytes[srcIndex + 2]; // R
                output[dstIndex + 3] = frameBytes[srcIndex + 3]; // A
            }
        }
    }

    private static void SavePng(byte[] pixels, string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var bitmap = BitmapSource.Create(
            ThumbnailWidth,
            ThumbnailHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            ThumbnailWidth * BytesPerPixel);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }
}
