namespace YMMProjectManager.Application.Thumbnails;

/// <summary>
/// タイムラインのシーク実行結果と検証に必要な前後フレーム情報です。
/// </summary>
//CSJ,record=SeekResult,フレーム移動結果
public sealed record SeekResult
{
    //CSJ,property=Success,成功
    public bool Success { get; init; }

    //CSJ,property=RequestedFrame,指定フレーム
    public int RequestedFrame { get; init; }

    //CSJ,property=BeforeFrame,変更前フレーム
    public int BeforeFrame { get; init; }

    //CSJ,property=AfterFrame,変更後フレーム
    public int AfterFrame { get; init; }

    //CSJ,property=FrameDelta,フレーム差分
    public int FrameDelta { get; init; }

    //CSJ,property=MethodUsed,使用したメソッド
    public string MethodUsed { get; init; } = "Failed";

    //CSJ,property=FailureReason,失敗理由
    public string? FailureReason { get; init; }

    //CSJ,property=ExceptionType,例外型
    public string? ExceptionType { get; init; }

    //CSJ,property=Duration,長さ
    public TimeSpan Duration { get; init; }

    //CSJ,property=Tolerance,許容差
    public int Tolerance { get; init; }

    //CSJ,property=Reason,理由
    public string? Reason => FailureReason;

    //CSJ,property=DurationMs,長さミリ秒
    public double DurationMs => Duration.TotalMilliseconds;

    /// <summary>
    /// シーク成功時に、要求フレームと実際の前後フレームを記録します。
    /// </summary>
    //CSJ,method=Succeeded,成功
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
    //CSJ,method=Succeeded,成功
    public static SeekResult Succeeded(int beforeFrame, int afterFrame)
        => Succeeded(afterFrame, beforeFrame, afterFrame, "CurrentFrameProperty", 0);

    /// <summary>
    /// シーク失敗時に、原因と観測できたフレーム情報を保持します。
    /// </summary>
    //CSJ,method=Failed,失敗
    public static SeekResult Failed(
        int requestedFrame,
        //CSJ,parameter=reason,理由
        string reason,
        //CSJ,parameter=beforeFrame,変更前フレーム
        int beforeFrame = 0,
        //CSJ,parameter=afterFrame,変更後フレーム
        int afterFrame = 0,
        string methodUsed = "Failed",
        //CSJ,parameter=exceptionType,例外型
        string? exceptionType = null,
        //CSJ,parameter=duration,長さ
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
    //CSJ,method=Failed,失敗
    public static SeekResult Failed(string reason, int beforeFrame = 0, int afterFrame = 0, TimeSpan? duration = null)
        => Failed(afterFrame, reason, beforeFrame, afterFrame, duration: duration);
}
