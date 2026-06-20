namespace YMMProjectManager.Application.Thumbnails;

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

    public static SeekResult Succeeded(int beforeFrame, int afterFrame)
        => Succeeded(afterFrame, beforeFrame, afterFrame, "CurrentFrameProperty", 0);

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

    public static SeekResult Failed(string reason, int beforeFrame = 0, int afterFrame = 0, TimeSpan? duration = null)
        => Failed(afterFrame, reason, beforeFrame, afterFrame, duration: duration);
}
