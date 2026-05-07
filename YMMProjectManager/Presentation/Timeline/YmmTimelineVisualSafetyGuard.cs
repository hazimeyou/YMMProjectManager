namespace YMMProjectManager.Presentation.Timeline;

public static class YmmTimelineVisualSafetyGuard
{
    public static bool IsVisualAttachAllowed(PureTimelineExperimentalOptions options)
        => !options.ForbidVisualTreeAttach;
}
