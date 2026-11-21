namespace YMMProjectManager.Application.Thumbnails;

/// <summary>
/// タイムラインのシーク実行結果と検証に必要な前後フレーム情報です。
/// </summary>
public sealed record SeekResult
{
    public bool Success { get; init; }

    public int RequestedFrame { get; init; }

    public int BeforeFrame { get; init; }

    public int AfterFrame { get; init; }

    public int FrameDelta { get; init; }

    public string MethodUsed { get; init; } = "Failed";

    public string? FailureReason { get; init; }

    public string? ExceptionType { get; init; }

    public TimeSpan Duration { get; init; }

    public int Tolerance { get; init; }

    public string? Reason => FailureReason;

    public double DurationMs => Duration.TotalMilliseconds;

    /// <summary>
    /// シーク成功時に、要求フレームと実際の前後フレームを記録します。
    /// </summary>
    public static SeekResult Succeeded(int requestedFrame, int beforeFrame, int afterFrame, string methodUsed, int tolerance)
        => new()
        {
            Success = true,
            RequestedFrame = requestedFrame,
            BeforeFrame = beforeFrame,
            AfterFrame = afterFrame,
            FrameDelta = requestedFrame - beforeFrame,
            MethodUsed = methodUsed,
            FailureReason = null,
            ExceptionType = null,
            Duration = TimeSpan.Zero,
            Tolerance = tolerance,
        };

    /// <summary>
    /// 互換用の簡易成功ファクトリです。
    /// </summary>
    public static SeekResult Succeeded(int beforeFrame, int afterFrame)
        => Succeeded(afterFrame, beforeFrame, afterFrame, "CurrentFrameProperty", 0);

    /// <summary>
    /// シーク失敗時に、原因と観測できたフレーム情報を保持します。
    /// </summary>
    public static SeekResult Failed(
        int requestedFrame,
        string reason,
        int beforeFrame = 0,
        int afterFrame = 0,
        string methodUsed = "Failed",
        string? exceptionType = null,
        TimeSpan? duration = null,
        int tolerance = 0)
        => new()
        {
            Success = false,
            RequestedFrame = requestedFrame,
            BeforeFrame = beforeFrame,
            AfterFrame = afterFrame,
            FrameDelta = requestedFrame - beforeFrame,
            MethodUsed = methodUsed,
            FailureReason = reason,
            ExceptionType = exceptionType,
            Duration = duration ?? TimeSpan.Zero,
            Tolerance = tolerance,
        };

    /// <summary>
    /// 呼び出し元が要求フレームを持たない場合の簡易失敗ファクトリです。
    /// </summary>
    public static SeekResult Failed(string reason, int beforeFrame = 0, int afterFrame = 0, TimeSpan? duration = null)
        => Failed(afterFrame, reason, beforeFrame, afterFrame, duration: duration);
}
