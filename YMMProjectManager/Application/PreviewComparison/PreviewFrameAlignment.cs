namespace YMMProjectManager.Application.PreviewComparison;

internal readonly record struct PreviewFrameRate(int Numerator, int Denominator);

/// <summary>A caller-issued identity for one Scene snapshot and one declared rendering configuration.</summary>
internal readonly record struct PreviewSource(
    Guid SnapshotId,
    Guid RenderingConfigurationId,
    PreviewFrameRate FrameRate,
    int FrameCount);

internal readonly record struct PreviewComparisonRequest(TimeSpan Timestamp, PreviewSource A, PreviewSource B);

internal enum PreviewComparisonError
{
    None,
    NegativeTimestamp,
    InvalidSource,
    SameSnapshot,
    RenderingConfigurationMismatch,
    OutsideCommonDuration,
    UnexpectedFrame,
    InvalidDimensions,
    InvalidBufferLength,
    DimensionMismatch,
    PixelFormatMismatch,
    UnsupportedPixelFormat,
}

internal enum PreviewComparisonSide { A, B }

internal sealed record PreviewFrameAlignment(
    PreviewComparisonRequest Request,
    int? FrameA,
    int? FrameB,
    PreviewComparisonError Error,
    PreviewComparisonSide? ErrorSide = null)
{
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
        int? frameB = Resolve(request.Timestamp, request.B);
        if (frameA is null || frameB is null)
        {
            PreviewComparisonSide? side = frameA.HasValue ? PreviewComparisonSide.B
                : frameB.HasValue ? PreviewComparisonSide.A : null;
            return new(request, frameA, frameB, PreviewComparisonError.OutsideCommonDuration, side);
        }

        return new(request, frameA, frameB, PreviewComparisonError.None);
    }

    private static bool IsValid(PreviewSource source) =>
        source.SnapshotId != Guid.Empty &&
        source.RenderingConfigurationId != Guid.Empty &&
        source.FrameRate.Numerator > 0 &&
        source.FrameRate.Denominator > 0 &&
        source.FrameCount >= 0;

    private static int? Resolve(TimeSpan timestamp, PreviewSource source)
    {
        // Integer division selects the containing frame. Its end belongs to the next frame.
        Int128 index = (Int128)timestamp.Ticks * source.FrameRate.Numerator /
            ((Int128)TimeSpan.TicksPerSecond * source.FrameRate.Denominator);
        return index < source.FrameCount ? (int)index : null;
    }
}
