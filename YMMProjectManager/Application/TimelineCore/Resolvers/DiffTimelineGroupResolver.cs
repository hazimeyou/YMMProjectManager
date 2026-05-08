namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineGroupResolver
{
    public static string ResolveGroupKey(DiffTimelineCoreItem item)
    {
        return item.FieldLabel switch
        {
            "テキスト" => "text",
            "素材パス" => "media-path",
            "フレーム" or "レイヤー" or "位置" => "timeline-move",
            "長さ" => "length",
            _ => "other",
        };
    }

    public static string ResolveGroupDisplayLabel(DiffTimelineCoreItem item)
    {
        return ResolveGroupDisplayLabelByKey(ResolveGroupKey(item));
    }

    public static string ResolveGroupDisplayLabelByKey(string groupKey)
    {
        return groupKey switch
        {
            "text" => "テキスト変更",
            "media-path" => "素材パス変更",
            "timeline-move" => "タイムライン移動",
            "length" => "長さ変更",
            _ => "その他",
        };
    }
}
