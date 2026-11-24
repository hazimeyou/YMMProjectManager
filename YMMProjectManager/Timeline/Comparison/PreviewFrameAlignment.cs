namespace YMMProjectManager.Application.PreviewComparison;

//CSJ,record=PreviewFrameRate,プレビューのフレームレート
internal readonly record struct PreviewFrameRate(int Numerator, int Denominator);

/// <summary>A caller-issued identity for one Scene snapshot and one declared rendering configuration.</summary>
//CSJ,record=PreviewSource,プレビュー元
internal readonly record struct PreviewSource(
    Guid SnapshotId,
    Guid RenderingConfigurationId,
    PreviewFrameRate FrameRate,
    int FrameCount);

//CSJ,record=PreviewComparisonRequest,プレビュー比較要求
internal readonly record struct PreviewComparisonRequest(
    TimeSpan Timestamp, PreviewSource A, PreviewSource B, TimeSpan Offset = default);

//CSJ,enum=PreviewComparisonError,プレビュー比較エラー
internal enum PreviewComparisonError
{
    None,
    NegativeTimestamp,
    //CSJ,field=InvalidSource,無効元
    InvalidSource,
    //CSJ,field=SameSnapshot,同一スナップショット
    SameSnapshot,
    //CSJ,field=RenderingConfigurationMismatch,描画構成不一致
    RenderingConfigurationMismatch,
    OutsideCommonDuration,
    UnexpectedFrame,
    InvalidDimensions,
    //CSJ,field=InvalidBufferLength,無効バッファー長さ
    InvalidBufferLength,
    DimensionMismatch,
    //CSJ,field=PixelFormatMismatch,画素形式不一致
    PixelFormatMismatch,
    //CSJ,field=UnsupportedPixelFormat,未対応画素形式
    UnsupportedPixelFormat,
}

//CSJ,enum=PreviewComparisonSide,プレビュー比較の左右区分
internal enum PreviewComparisonSide { A, B }

//CSJ,record=PreviewFrameAlignment,プレビュー比較の時刻合わせ
internal sealed record PreviewFrameAlignment(
    PreviewComparisonRequest Request,
    int? FrameA,
    int? FrameB,
    PreviewComparisonError Error,
    PreviewComparisonSide? ErrorSide = null)
{
    //CSJ,method=Align,位置をそろえる
    public static PreviewFrameAlignment Align(PreviewComparisonRequest request)
    {
        if (request.Timestamp < TimeSpan.Zero)
            return new(request, null, null, PreviewComparisonError.NegativeTimestamp);
        if (!IsValid(request.A))
            return new(request, null, null, PreviewComparisonError.InvalidSource, PreviewComparisonSide.A);
        if (!IsValid(request.B))
            return new(request, null, null, PreviewComparisonError.InvalidSource, PreviewComparisonSide.B);
        if (request.A.SnapshotId == request.B.SnapshotId)
            return new(request, null, null, PreviewComparisonError.SameSnapshot);
        if (request.A.RenderingConfigurationId != request.B.RenderingConfigurationId)
            return new(request, null, null, PreviewComparisonError.RenderingConfigurationMismatch);

        int? frameA = Resolve(request.Timestamp, request.A);
        var bTicks = (Int128)request.Timestamp.Ticks + request.Offset.Ticks;
        int? frameB = bTicks >= 0 && bTicks <= long.MaxValue
            ? Resolve(TimeSpan.FromTicks((long)bTicks), request.B)
            : null;
        if (frameA is null || frameB is null)
        {
            PreviewComparisonSide? side = frameA.HasValue ? PreviewComparisonSide.B
                : frameB.HasValue ? PreviewComparisonSide.A : null;
            return new(request, frameA, frameB, PreviewComparisonError.OutsideCommonDuration, side);
        }

        return new(request, frameA, frameB, PreviewComparisonError.None);
    }

    //CSJ,method=IsValid,有効かを判定する
    private static bool IsValid(PreviewSource source) =>
        source.SnapshotId != Guid.Empty &&
        source.RenderingConfigurationId != Guid.Empty &&
        source.FrameRate.Numerator > 0 &&
        source.FrameRate.Denominator > 0 &&
        source.FrameCount >= 0;

    //CSJ,method=Resolve,解決する
    private static int? Resolve(TimeSpan timestamp, PreviewSource source)
    {
        // Integer division selects the containing frame. Its end belongs to the next frame.
        //CSJ,variable=index,インデックス
        Int128 index = (Int128)timestamp.Ticks * source.FrameRate.Numerator /
            ((Int128)TimeSpan.TicksPerSecond * source.FrameRate.Denominator);
        return index < source.FrameCount ? (int)index : null;
    }
}
