namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineCoreLabelResolver
{
    public static string ToDiffKindLabel(string kind)
    {
        return kind switch
        {
            "Added" => "追加",
            "Removed" => "削除",
            "Moved" => "移動",
            "Changed" => "変更",
            _ => kind,
        };
    }

    public static string ToFieldLabel(object? field)
    {
        var value = field?.ToString() ?? string.Empty;
        return value switch
        {
            "Text" => "テキスト",
            "FilePath" => "素材パス",
            "Frame" => "フレーム",
            "Layer" => "レイヤー",
            "Length" => "長さ",
            _ => value,
        };
    }
}
