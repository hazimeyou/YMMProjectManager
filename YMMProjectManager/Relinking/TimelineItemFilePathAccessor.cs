using YukkuriMovieMaker.Project.Items;

namespace YMMProjectManager.Infrastructure;

//CSJ,class=TimelineItemFilePathAccessor,タイムライン項目のファイルパス参照
internal static class TimelineItemFilePathAccessor
{
    //CSJ,method=TryGetFilePath,ファイルパスの取得を試みる
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

    //CSJ,method=SetFilePath,ファイルパスを設定する
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
