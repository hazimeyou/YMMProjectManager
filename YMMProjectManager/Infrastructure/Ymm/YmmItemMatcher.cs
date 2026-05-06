namespace YMMProjectManager.Infrastructure.Ymm;

public static class YmmItemMatcher
{
    public static string BuildKey(YmmItemModel item)
    {
        var frame = item.Fields.GetValueOrDefault("Frame") ?? string.Empty;
        var layer = item.Fields.GetValueOrDefault("Layer") ?? string.Empty;
        var text = item.Fields.GetValueOrDefault("Text") ?? string.Empty;
        var filePath = item.Fields.GetValueOrDefault("FilePath") ?? string.Empty;
        return $"{item.Scope}|{frame}|{layer}|{text}|{filePath}";
    }
}
