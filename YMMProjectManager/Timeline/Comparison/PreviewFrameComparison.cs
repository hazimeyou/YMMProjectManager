namespace YMMProjectManager.Application.PreviewComparison;

//CSJ,enum=PreviewPixelFormat,プレビュー画素形式
internal enum PreviewPixelFormat { Unknown, Bgra32, Pbgra32 }

/// <summary>Packed, top-down pixels. The caller keeps the buffer stable throughout comparison.</summary>
//CSJ,record=PreviewFrame,プレビューフレーム
internal readonly record struct PreviewFrame(
    PreviewSource Source,
    int FrameIndex,
    int Width,
    int Height,
    PreviewPixelFormat Format,
    ReadOnlyMemory<byte> Pixels);

//CSJ,record=PreviewImageMetrics,プレビュー画像測定値
internal sealed record PreviewImageMetrics(
    int Width,
    int Height,
    int ChangedPixels,
    byte MaximumChannelDifference,
    long TotalAbsoluteChannelDifference)
{
    //CSJ,property=PixelCount,画素件数
    private long PixelCount => (long)Width * Height;

    //CSJ,property=ExactlyEqual,完全一致
    public bool ExactlyEqual => ChangedPixels == 0;
    //CSJ,property=ChangedPixelPercentage,変化した画素の割合
    public double ChangedPixelPercentage => 100d * ChangedPixels / PixelCount;
    //CSJ,property=MeanAbsoluteError,平均絶対誤差
    public double MeanAbsoluteError => (double)TotalAbsoluteChannelDifference / (4 * PixelCount);
}

//CSJ,record=PreviewComparisonResult,プレビュー比較結果
internal sealed record PreviewComparisonResult(
    PreviewFrameAlignment Alignment,
    PreviewImageMetrics? Metrics,
    PreviewComparisonError Error,
    PreviewComparisonSide? ErrorSide = null);

//CSJ,class=PreviewFrameComparison,プレビューフレーム比較
internal static class PreviewFrameComparison
{
    private const int ChannelsPerPixel = 4;
    private const int PixelsPerCancellationCheck = 4096;

    //CSJ,method=Compare,比較する
    public static PreviewComparisonResult Compare(
        //CSJ,parameter=request,要求
        PreviewComparisonRequest request,
        PreviewFrame a,
        PreviewFrame b,
        //CSJ,parameter=cancellationToken,キャンセルトークン
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        //CSJ,variable=alignment,整合
        var alignment = PreviewFrameAlignment.Align(request);
        if (alignment.Error != PreviewComparisonError.None)
            return new(alignment, null, alignment.Error, alignment.ErrorSide);

        //CSJ,variable=error,エラー
        var error = ValidateFrame(a, request.A, alignment.FrameA!.Value);
        if (error != PreviewComparisonError.None)
            return new(alignment, null, error, PreviewComparisonSide.A);
        error = ValidateFrame(b, request.B, alignment.FrameB!.Value);
        if (error != PreviewComparisonError.None)
            return new(alignment, null, error, PreviewComparisonSide.B);
        if (a.Width != b.Width || a.Height != b.Height)
            return new(alignment, null, PreviewComparisonError.DimensionMismatch);
        if (a.Format != b.Format)
            return new(alignment, null, PreviewComparisonError.PixelFormatMismatch);
        if (a.Format != PreviewPixelFormat.Bgra32)
            return new(alignment, null, PreviewComparisonError.UnsupportedPixelFormat);

        //CSJ,variable=expectedLength,期待値長さ
        long expectedLength = (long)a.Width * a.Height * ChannelsPerPixel;
        if (a.Pixels.Length != expectedLength)
            return new(alignment, null, PreviewComparisonError.InvalidBufferLength, PreviewComparisonSide.A);
        if (b.Pixels.Length != expectedLength)
            return new(alignment, null, PreviewComparisonError.InvalidBufferLength, PreviewComparisonSide.B);

        //CSJ,variable=metrics,測定値
        var metrics = ComparePixels(a.Width, a.Height, a.Pixels.Span, b.Pixels.Span, cancellationToken);
        return new(alignment, metrics, PreviewComparisonError.None);
    }

    //CSJ,method=ValidateFrame,フレームを検証する
    private static PreviewComparisonError ValidateFrame(PreviewFrame frame, PreviewSource source, int index)
    {
        if (frame.Source != source || frame.FrameIndex != index)
            return PreviewComparisonError.UnexpectedFrame;

        //CSJ,variable=pixelCount,画素件数
        long pixelCount = (long)frame.Width * frame.Height;
        if (frame.Width <= 0 || frame.Height <= 0 || pixelCount > int.MaxValue / ChannelsPerPixel)
            return PreviewComparisonError.InvalidDimensions;

        return PreviewComparisonError.None;
    }

    //CSJ,method=ComparePixels,画素列を比較する
    private static PreviewImageMetrics ComparePixels(
        //CSJ,parameter=width,幅
        int width, int height, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, CancellationToken cancellationToken)
    {
        long sum = 0;
        //CSJ,variable=maximum,最大
        int maximum = 0;
        //CSJ,variable=changedPixels,変更済み画素列
        int changedPixels = 0;
        //CSJ,variable=offset,オフセット
        for (int offset = 0; offset < a.Length; offset += ChannelsPerPixel)
        {
            if (offset % (PixelsPerCancellationCheck * ChannelsPerPixel) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            //CSJ,variable=pixelMaximum,画素最大
            int pixelMaximum = 0;
            //CSJ,variable=channel,チャンネル
            for (int channel = 0; channel < ChannelsPerPixel; channel++)
            {
                int difference = Math.Abs(a[offset + channel] - b[offset + channel]);
                sum += difference;
                pixelMaximum = Math.Max(pixelMaximum, difference);
            }

            maximum = Math.Max(maximum, pixelMaximum);
            if (pixelMaximum != 0)
                changedPixels++;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(width, height, changedPixels, (byte)maximum, sum);
    }
}
