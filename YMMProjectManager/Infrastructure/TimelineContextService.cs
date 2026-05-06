
namespace YMMProjectManager.Infrastructure;

public static class TimelineContextService
{
    public static TimelineToolInfo? Info { get; set; }
    public static Timeline? Timeline => Info?.Timeline;
}
