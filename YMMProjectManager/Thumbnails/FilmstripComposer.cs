using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YMMProjectManager.Infrastructure.Output;

/// <summary>
/// 複数フレームの中央列を 64x36 の 1 枚画像へ並べるフィルムストリップ合成器です。
/// </summary>
//CSJ,class=FilmstripComposer,フィルムストリップ作成処理
public sealed class FilmstripComposer
{
    //CSJ,field=OutputWidth,出力幅
    public const int OutputWidth = 64;
    //CSJ,field=OutputHeight,出力高さ
    public const int OutputHeight = 36;
    private const int BytesPerPixel = 4;

    //CSJ,field=outputPixels,出力画素列
    private readonly byte[] outputPixels = new byte[OutputWidth * OutputHeight * BytesPerPixel];

    //CSJ,method=SetColumnFromFrame,フレーム画像から列を設定する
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

        // 各フレームから中央列だけを抜き出し、横方向の時間変化として並べる。
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

    //CSJ,method=FillMissingColumns,欠けた列を補う
    public void FillMissingColumns()
    {
        // 取得に失敗した列は直前列で埋め、スクラブ画像の透明な穴を避ける。
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

    //CSJ,method=SavePng,PNGを保存する
    public void SavePng(string path)
    {
        // outputPixels は BGRA32 前提で保持しているため、そのまま PNG エンコードする。
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        //CSJ,variable=bitmap,ビットマップ
        var bitmap = BitmapSource.Create(
            OutputWidth,
            OutputHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            outputPixels,
            OutputWidth * BytesPerPixel);

        //CSJ,variable=encoder,エンコーダー
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        //CSJ,variable=stream,ストリーム
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}

