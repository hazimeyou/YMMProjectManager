namespace YMMProjectManager.Application.PreviewComparison;

/// <summary>Borrowed, top-down rows. Full stride bytes, including final row padding, must be readable.</summary>
//CSJ,record=SceneRenderResponse,シーン描画応答
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
//CSJ,class=SceneCapturedFrame,取得済みシーンフレーム
internal sealed class SceneCapturedFrame
{
    //CSJ,field=MaxBufferBytes,最大バッファーバイト列
    private const int MaxBufferBytes = 32 * 1024 * 1024;
    private const int BytesPerPixel = 4;
    private const int BytesPerCancellationCheck = 64 * 1024;

    //CSJ,field=frame,フレーム
    private readonly PreviewFrame frame;

    private SceneCapturedFrame(SceneRenderRequest request, byte[] pixels)
    {
        Request = request;
        frame = new(request.Snapshot.Source, request.FrameIndex, request.Width, request.Height, PreviewPixelFormat.Bgra32, pixels);
    }

    //CSJ,property=Request,要求
    public SceneRenderRequest Request { get; }

    //CSJ,method=CheckSize,サイズを確認する
    internal static SceneHandoffError CheckSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return SceneHandoffError.InvalidDimensions;
        return (long)width * height > MaxBufferBytes / BytesPerPixel
            ? SceneHandoffError.FrameTooLarge
            : SceneHandoffError.None;
    }

    //CSJ,method=Copy,コピーする
    public static (SceneCapturedFrame? Frame, SceneHandoffError Error) Copy(
        //CSJ,parameter=plan,計画
        SceneComparisonPlan plan, PreviewComparisonSide side, SceneRenderResponse response,
        //CSJ,parameter=cancellationToken,キャンセルトークン
        CancellationToken cancellationToken = default) =>
        CopyCore(plan, side, response, PreviewPixelFormat.Bgra32, cancellationToken);

    // Experimental byte conversion. The coordinator uses the straight-only Copy contract.
    internal static (SceneCapturedFrame? Frame, SceneHandoffError Error) CopyExperimentalPbgra32(
        //CSJ,parameter=plan,計画
        SceneComparisonPlan plan, PreviewComparisonSide side, SceneRenderResponse response,
        //CSJ,parameter=cancellationToken,キャンセルトークン
        CancellationToken cancellationToken = default) =>
        CopyCore(plan, side, response, PreviewPixelFormat.Pbgra32, cancellationToken);

    //CSJ,method=CopyCore,中核をコピーする
    private static (SceneCapturedFrame? Frame, SceneHandoffError Error) CopyCore(
        //CSJ,parameter=plan,計画
        SceneComparisonPlan plan, PreviewComparisonSide side, SceneRenderResponse response,
        //CSJ,parameter=requiredFormat,必須書式化
        PreviewPixelFormat requiredFormat, CancellationToken cancellationToken)
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

        //CSJ,variable=error,エラー
        var error = request.Snapshot.CheckObserved(response.ObservedSnapshot);
        if (error != SceneHandoffError.None)
            return (null, error);
        error = CheckSize(response.Width, response.Height);
        if (error != SceneHandoffError.None)
            return (null, error);
        if (response.Width != request.Width || response.Height != request.Height)
            return (null, SceneHandoffError.InvalidDimensions);
        if (response.Format != requiredFormat)
            return (null, SceneHandoffError.UnsupportedPixelFormat);

        //CSJ,variable=rowBytes,行バイト列
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
        //CSJ,variable=owned,管理対象の
        var owned = new byte[rowBytes * response.Height];
        if (requiredFormat == PreviewPixelFormat.Pbgra32)
        {
            for (int row = 0; row < response.Height; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryUnpremultiply(source.Slice(row * response.Stride, rowBytes),
                        owned.AsSpan(row * rowBytes, rowBytes), cancellationToken))
                    return (null, SceneHandoffError.InvalidPremultipliedPixel);
            }
        }
        else if (rowBytes == response.Stride)
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
        //CSJ,variable=offset,オフセット
        for (int offset = 0; offset < source.Length; offset += BytesPerCancellationCheck)
        {
            cancellationToken.ThrowIfCancellationRequested();
            //CSJ,variable=count,件数
            int count = Math.Min(BytesPerCancellationCheck, source.Length - offset);
            source.Slice(offset, count).CopyTo(destination.Slice(offset, count));
        }
    }

    private static bool TryUnpremultiply(ReadOnlySpan<byte> source, Span<byte> destination, CancellationToken token)
    {
        //CSJ,variable=offset,オフセット
        for (int offset = 0; offset < source.Length; offset += BytesPerPixel)
        {
            if (offset % BytesPerCancellationCheck == 0)
                token.ThrowIfCancellationRequested();

            //CSJ,variable=alpha,アルファ
            int alpha = source[offset + 3];
            //CSJ,variable=channel,チャンネル
            for (int channel = 0; channel < 3; channel++)
            {
                int premultiplied = source[offset + channel];
                if (premultiplied > alpha)
                    return false;
                destination[offset + channel] = alpha == 0 ? (byte)0
                    : (byte)((premultiplied * 255 + alpha / 2) / alpha);
            }

            destination[offset + 3] = (byte)alpha;
        }

        return true;
    }

    internal bool MatchesReplay(SceneCapturedFrame replay, CancellationToken token)
    {
        if (Request.OperationId == replay.Request.OperationId ||
            Request != (replay.Request with { OperationId = Request.OperationId }))
            return false;

        var first = frame.Pixels.Span;
        var second = replay.frame.Pixels.Span;
        //CSJ,variable=offset,オフセット
        for (int offset = 0; offset < first.Length; offset += BytesPerCancellationCheck)
        {
            token.ThrowIfCancellationRequested();
            //CSJ,variable=count,件数
            int count = Math.Min(BytesPerCancellationCheck, first.Length - offset);
            if (!first.Slice(offset, count).SequenceEqual(second.Slice(offset, count)))
                return false;
        }

        token.ThrowIfCancellationRequested();
        return true;
    }

    //CSJ,method=Compare,比較する
    internal static PreviewComparisonResult Compare(
        //CSJ,parameter=request,要求
        PreviewComparisonRequest request, SceneCapturedFrame a, SceneCapturedFrame b, CancellationToken cancellationToken) =>
        PreviewFrameComparison.Compare(request, a.frame, b.frame, cancellationToken);
}
