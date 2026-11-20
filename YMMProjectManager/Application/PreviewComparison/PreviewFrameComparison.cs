namespace YMMProjectManager.Application.PreviewComparison;

internal enum PreviewPixelFormat { Unknown, Bgra32, Pbgra32 }

/// <summary>Packed, top-down pixels. The caller keeps the buffer stable throughout comparison.</summary>
internal readonly record struct PreviewFrame(
    PreviewSource Source,
    int FrameIndex,
    int Width,
    int Height,
    PreviewPixelFormat Format,
    ReadOnlyMemory<byte> Pixels);

internal sealed record PreviewImageMetrics(
    int Width,
    int Height,
    int ChangedPixels,
    byte MaximumChannelDifference,
    long TotalAbsoluteChannelDifference)
{
    private long PixelCount => (long)Width * Height;

    public bool ExactlyEqual => ChangedPixels == 0;
    public double ChangedPixelPercentage => 100d * ChangedPixels / PixelCount;
    public double MeanAbsoluteError => (double)TotalAbsoluteChannelDifference / (4 * PixelCount);
}

internal sealed record PreviewComparisonResult(
    PreviewFrameAlignment Alignment,
    PreviewImageMetrics? Metrics,
    PreviewComparisonError Error,
    PreviewComparisonSide? ErrorSide = null);

internal static class PreviewFrameComparison
{
    private const int ChannelsPerPixel = 4;
    private const int PixelsPerCancellationCheck = 4096;

    public static PreviewComparisonResult Compare(
        PreviewComparisonRequest request,
        PreviewFrame a,
        PreviewFrame b,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var alignment = PreviewFrameAlignment.Align(request);
        if (alignment.Error != PreviewComparisonError.None)
            return new(alignment, null, alignment.Error, alignment.ErrorSide);

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

        long expectedLength = (long)a.Width * a.Height * ChannelsPerPixel;
        if (a.Pixels.Length != expectedLength)
            return new(alignment, null, PreviewComparisonError.InvalidBufferLength, PreviewComparisonSide.A);
        if (b.Pixels.Length != expectedLength)
            return new(alignment, null, PreviewComparisonError.InvalidBufferLength, PreviewComparisonSide.B);

        var metrics = ComparePixels(a.Width, a.Height, a.Pixels.Span, b.Pixels.Span, cancellationToken);
        return new(alignment, metrics, PreviewComparisonError.None);
    }

    private static PreviewComparisonError ValidateFrame(PreviewFrame frame, PreviewSource source, int index)
    {
        if (frame.Source != source || frame.FrameIndex != index)
            return PreviewComparisonError.UnexpectedFrame;

        long pixelCount = (long)frame.Width * frame.Height;
        if (frame.Width <= 0 || frame.Height <= 0 || pixelCount > int.MaxValue / ChannelsPerPixel)
            return PreviewComparisonError.InvalidDimensions;

        return PreviewComparisonError.None;
    }

    private static PreviewImageMetrics ComparePixels(
        int width, int height, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, CancellationToken cancellationToken)
    {
        long sum = 0;
        int maximum = 0;
        int changedPixels = 0;
        for (int offset = 0; offset < a.Length; offset += ChannelsPerPixel)
        {
            if (offset % (PixelsPerCancellationCheck * ChannelsPerPixel) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            int pixelMaximum = 0;
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
