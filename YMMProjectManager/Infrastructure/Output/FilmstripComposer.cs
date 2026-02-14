using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YMMProjectManager.Infrastructure.Output;

public sealed class FilmstripComposer
{
    public const int OutputWidth = 64;
    public const int OutputHeight = 36;
    private const int BytesPerPixel = 4;

    private readonly byte[] outputPixels = new byte[OutputWidth * OutputHeight * BytesPerPixel];

    public void SetColumnFromFrame(byte[] frameBytes, int frameWidth, int frameHeight, int column)
    {
        if (column < 0 || column >= OutputWidth)
        {
            return;
        }

        if (frameWidth <= 0 || frameHeight <= 0)
        {
            return;
        }

        if (frameBytes.Length < frameWidth * frameHeight * BytesPerPixel)
        {
            return;
        }

        // Fast path: pick center X from source frame and scale Y to 36.
        var sourceX = frameWidth / 2;
        for (var y = 0; y < OutputHeight; y++)
        {
            var sourceY = OutputHeight == 1
                ? 0
                : (int)Math.Round(y * (frameHeight - 1d) / (OutputHeight - 1d));

            var srcIndex = ((sourceY * frameWidth) + sourceX) * BytesPerPixel;
            var dstIndex = ((y * OutputWidth) + column) * BytesPerPixel;
            outputPixels[dstIndex + 0] = frameBytes[srcIndex + 0]; // B
            outputPixels[dstIndex + 1] = frameBytes[srcIndex + 1]; // G
            outputPixels[dstIndex + 2] = frameBytes[srcIndex + 2]; // R
            outputPixels[dstIndex + 3] = frameBytes[srcIndex + 3]; // A
        }
    }

    public void FillMissingColumns()
    {
        for (var x = 1; x < OutputWidth; x++)
        {
            var hasAnyPixel = false;
            for (var y = 0; y < OutputHeight; y++)
            {
                var idx = ((y * OutputWidth) + x) * BytesPerPixel;
                if (outputPixels[idx + 3] != 0)
                {
                    hasAnyPixel = true;
                    break;
                }
            }

            if (hasAnyPixel)
            {
                continue;
            }

            for (var y = 0; y < OutputHeight; y++)
            {
                var prev = ((y * OutputWidth) + (x - 1)) * BytesPerPixel;
                var cur = ((y * OutputWidth) + x) * BytesPerPixel;
                outputPixels[cur + 0] = outputPixels[prev + 0];
                outputPixels[cur + 1] = outputPixels[prev + 1];
                outputPixels[cur + 2] = outputPixels[prev + 2];
                outputPixels[cur + 3] = outputPixels[prev + 3];
            }
        }
    }

    public void SavePng(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var bitmap = BitmapSource.Create(
            OutputWidth,
            OutputHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            outputPixels,
            OutputWidth * BytesPerPixel);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}

