namespace YMMProjectManager.Application.PreviewComparison;

/// <summary>Borrowed, top-down rows. Full stride bytes, including final row padding, must be readable.</summary>
internal readonly record struct SceneRenderResponse(
    Guid OperationId,
    SceneSnapshot ObservedSnapshot,
    int FrameIndex,
    int Width,
    int Height,
    int Stride,
    PreviewPixelFormat Format,
    ReadOnlyMemory<byte> Pixels);

/// <summary>Owns a packed copy of a single render response. Does not expose the pixel storage.</summary>
internal sealed class SceneCapturedFrame
{
    private const int MaxBufferBytes = 32 * 1024 * 1024;
    private const int BytesPerPixel = 4;
    private const int BytesPerCancellationCheck = 64 * 1024;

    private readonly PreviewFrame frame;

    private SceneCapturedFrame(SceneRenderRequest request, byte[] pixels)
    {
        Request = request;
        frame = new(request.Snapshot.Source, request.FrameIndex, request.Width, request.Height, PreviewPixelFormat.Bgra32, pixels);
    }

    public SceneRenderRequest Request { get; }

    internal static SceneHandoffError CheckSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return SceneHandoffError.InvalidDimensions;
        return (long)width * height > MaxBufferBytes / BytesPerPixel
            ? SceneHandoffError.FrameTooLarge
            : SceneHandoffError.None;
    }

    public static (SceneCapturedFrame? Frame, SceneHandoffError Error) Copy(
        SceneComparisonPlan plan, PreviewComparisonSide side, SceneRenderResponse response,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SceneRenderRequest? issued = side switch
        {
            PreviewComparisonSide.A => plan.A,
            PreviewComparisonSide.B => plan.B,
            _ => null,
        };
        if (issued is not { } request || response.OperationId != request.OperationId)
            return (null, SceneHandoffError.OperationMismatch);
        if (response.FrameIndex != request.FrameIndex)
            return (null, SceneHandoffError.FrameMismatch);

        var error = request.Snapshot.CheckObserved(response.ObservedSnapshot);
        if (error != SceneHandoffError.None)
            return (null, error);
        error = CheckSize(response.Width, response.Height);
        if (error != SceneHandoffError.None)
            return (null, error);
        if (response.Width != request.Width || response.Height != request.Height)
            return (null, SceneHandoffError.InvalidDimensions);
        if (response.Format != PreviewPixelFormat.Bgra32)
            return (null, SceneHandoffError.UnsupportedPixelFormat);

        int rowBytes = response.Width * BytesPerPixel;
        if (response.Stride < rowBytes)
            return (null, SceneHandoffError.InvalidStride);
        long borrowedLength = (long)response.Stride * response.Height;
        if (borrowedLength > MaxBufferBytes)
            return (null, SceneHandoffError.FrameTooLarge);
        if (response.Pixels.Length != borrowedLength)
            return (null, SceneHandoffError.InvalidBufferLength);

        var source = response.Pixels.Span;
        cancellationToken.ThrowIfCancellationRequested();
        var owned = new byte[rowBytes * response.Height];
        if (rowBytes == response.Stride)
        {
            CopyWithCancellation(source, owned, cancellationToken);
        }
        else
        {
            for (int row = 0; row < response.Height; row++)
            {
                CopyWithCancellation(source.Slice(row * response.Stride, rowBytes),
                    owned.AsSpan(row * rowBytes, rowBytes), cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (new(request, owned), SceneHandoffError.None);
    }

    private static void CopyWithCancellation(ReadOnlySpan<byte> source, Span<byte> destination, CancellationToken cancellationToken)
    {
        for (int offset = 0; offset < source.Length; offset += BytesPerCancellationCheck)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(BytesPerCancellationCheck, source.Length - offset);
            source.Slice(offset, count).CopyTo(destination.Slice(offset, count));
        }
    }

    internal static PreviewComparisonResult Compare(
        PreviewComparisonRequest request, SceneCapturedFrame a, SceneCapturedFrame b, CancellationToken cancellationToken) =>
        PreviewFrameComparison.Compare(request, a.frame, b.frame, cancellationToken);
}
