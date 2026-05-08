namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineDisplayLabelResolver
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
            "Position" => "位置",
            _ => value,
        };
    }

    public static string ToPathLabel(string scope)
    {
        return string.IsNullOrWhiteSpace(scope) ? "(scopeなし)" : scope;
    }

    public static string ToSemanticCategory(string kindLabel, string fieldLabel)
    {
        if (fieldLabel is "フレーム" or "レイヤー" or "位置")
        {
            return "TimelinePosition";
        }

        if (fieldLabel is "素材パス")
        {
            return "MediaPath";
        }

        if (fieldLabel is "テキスト")
        {
            return "Text";
        }

        if (fieldLabel is "長さ")
        {
            return "Duration";
        }

        return kindLabel switch
        {
            "追加" => "Added",
            "削除" => "Removed",
            "移動" => "Moved",
            "変更" => "Changed",
            _ => "Other",
        };
    }

    public static string ToDisplayLabel(string kindLabel, string fieldLabel)
    {
        return $"{kindLabel} {fieldLabel}";
    }
}
