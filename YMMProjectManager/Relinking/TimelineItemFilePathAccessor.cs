using YukkuriMovieMaker.Project.Items;

namespace YMMProjectManager.Infrastructure;

internal static class TimelineItemFilePathAccessor
{
    public static bool TryGetFilePath(IItem item, out string? path)
    {
        switch (item)
        {
            case VideoItem videoItem:
                path = videoItem.FilePath;
                return true;
            case AudioItem audioItem:
                path = audioItem.FilePath;
                return true;
            case ImageItem imageItem:
                path = imageItem.FilePath;
                return true;
            default:
                path = null;
                return false;
        }
    }

    public static void SetFilePath(IItem item, string path)
    {
        switch (item)
        {
            case VideoItem videoItem:
                videoItem.FilePath = path;
                break;
            case AudioItem audioItem:
                audioItem.FilePath = path;
                break;
            case ImageItem imageItem:
                imageItem.FilePath = path;
                break;
            default:
                throw new NotSupportedException($"Unsupported item type: {item.GetType().FullName}");
        }
    }
}
