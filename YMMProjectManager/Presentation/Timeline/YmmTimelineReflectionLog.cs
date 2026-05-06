namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmTimelineReflectionLog
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    public string Category { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public override string ToString()
    {
        return $"{Timestamp:HH:mm:ss.fff} [{Category}] {Message}";
    }
}
