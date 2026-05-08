namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineFilterResolver
{
    public static string ResolveFilterKey(DiffTimelineCoreItem item)
    {
        return $"kind:{item.KindLabel}|semantic:{item.SemanticCategory}|group:{item.GroupKey}";
    }

    public static Func<DiffTimelineCoreItem, bool> BuildPassThroughFilter()
    {
        return static _ => true;
    }
}
