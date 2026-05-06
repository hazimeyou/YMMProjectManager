namespace YMMProjectManager.Presentation.Timeline;

public sealed class PureTimelineAdapterResult
{
    public bool Succeeded { get; set; }

    public string Message { get; set; } = string.Empty;

    public Exception? Exception { get; set; }

    public static PureTimelineAdapterResult Ok(string message = "")
    {
        return new PureTimelineAdapterResult
        {
            Succeeded = true,
            Message = message,
        };
    }

    public static PureTimelineAdapterResult Fail(string message, Exception? exception = null)
    {
        return new PureTimelineAdapterResult
        {
            Succeeded = false,
            Message = message,
            Exception = exception,
        };
    }
}
