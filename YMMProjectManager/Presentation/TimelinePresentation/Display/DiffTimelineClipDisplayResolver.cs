namespace YMMProjectManager.Presentation.TimelinePresentation.Display;

internal static class DiffTimelineClipDisplayResolver
{
    public static string BuildClipTitle(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return "(unnamed)";
        }

        return displayName.Length <= 48 ? displayName : $"{displayName[..48]}…";
    }

    public static string ResolveClipTypeLabel(string displayName, string category, string? oldValue, string? newValue)
    {
        var source = string.Join(" ", new[] { displayName, category, oldValue, newValue }).ToLowerInvariant();
        if (source.Contains("text") || source.Contains("テキスト") || source.Contains("字幕") || source.Contains("serif")) return "テキスト";
        if (source.Contains("shape") || source.Contains("rectangle") || source.Contains("ellipse") || source.Contains("図形") || source.Contains("四角")) return "図形";
        if (source.Contains("audio") || source.Contains("voice") || source.Contains("音声") || source.Contains("wav") || source.Contains("mp3")) return "音声";
        if (source.Contains("image") || source.Contains("画像") || source.Contains(".png") || source.Contains(".jpg") || source.Contains(".jpeg") || source.Contains(".bmp")) return "画像";
        if (source.Contains("video") || source.Contains("動画") || source.Contains(".mp4") || source.Contains(".mov") || source.Contains(".avi")) return "動画";
        return "その他";
    }
}
