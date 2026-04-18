using System.IO;
using Microsoft.Win32;
using YMMProjectManager.Infrastructure.Output;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project.Items;

namespace YMMProjectManager.Infrastructure;

public enum TimelineItemRelinkResultKind
{
    Success,
    Info,
    Warning,
}

public sealed class TimelineItemRelinkService
{
    private readonly FileLogger logger;

    public TimelineItemRelinkService(FileLogger logger)
    {
        this.logger = logger;
    }

    public (TimelineItemRelinkResultKind Kind, string Message) ReplaceSelectedItemFilePath(TimelineToolInfo? info)
    {
        if (info?.Timeline is null)
        {
            return (TimelineItemRelinkResultKind.Info, "YMM\u3067\u30d7\u30ed\u30b8\u30a7\u30af\u30c8\u3092\u958b\u3044\u305f\u72b6\u614b\u3067\u5b9f\u884c\u3057\u3066\u304f\u3060\u3055\u3044\u3002");
        }

        var selectedItems = info.Timeline.SelectedItems;
        if (selectedItems.Count == 0)
        {
            return (TimelineItemRelinkResultKind.Info, "\u30bf\u30a4\u30e0\u30e9\u30a4\u30f3\u4e0a\u306e\u30a2\u30a4\u30c6\u30e0\u30921\u3064\u9078\u629e\u3057\u3066\u304f\u3060\u3055\u3044\u3002");
        }

        if (selectedItems.Count > 1)
        {
            return (TimelineItemRelinkResultKind.Info, "\u8907\u6570\u9078\u629e\u306b\u306f\u672a\u5bfe\u5fdc\u3067\u3059\u3002\u30a2\u30a4\u30c6\u30e0\u30921\u3064\u3060\u3051\u9078\u629e\u3057\u3066\u304f\u3060\u3055\u3044\u3002");
        }

        var item = selectedItems[0];
        var currentPath = TimelineItemFilePathAccessor.TryGetFilePath(item, out var path) ? path : null;
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            logger.Info("Timeline item relink skipped. reason=item has no editable FilePath.");
            return (TimelineItemRelinkResultKind.Warning, "\u9078\u629e\u4e2d\u306e\u30a2\u30a4\u30c6\u30e0\u306f\u30d5\u30a1\u30a4\u30eb\u30d1\u30b9\u5909\u66f4\u306b\u5bfe\u5fdc\u3057\u3066\u3044\u307e\u305b\u3093\u3002\u753b\u50cf\u30fb\u52d5\u753b\u30fb\u97f3\u58f0\u30a2\u30a4\u30c6\u30e0\u3092\u9078\u629e\u3057\u3066\u304f\u3060\u3055\u3044\u3002");
        }

        var dialog = new OpenFileDialog
        {
            Title = "\u5dee\u3057\u66ff\u3048\u308b\u30d5\u30a1\u30a4\u30eb\u3092\u9078\u629e",
            CheckFileExists = true,
            FileName = Path.GetFileName(currentPath),
            Filter = BuildFilter(item),
            InitialDirectory = ResolveInitialDirectory(currentPath),
        };

        if (dialog.ShowDialog() != true)
        {
            return (TimelineItemRelinkResultKind.Info, "\u30ad\u30e3\u30f3\u30bb\u30eb\u3057\u307e\u3057\u305f\u3002");
        }

        var newPath = dialog.FileName;
        if (string.Equals(currentPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            return (TimelineItemRelinkResultKind.Info, "\u5909\u66f4\u306f\u3042\u308a\u307e\u305b\u3093\u3002");
        }

        try
        {
            TimelineItemFilePathAccessor.SetFilePath(item, newPath);
            info.UndoRedoManager?.Record();
            logger.Info($"Timeline item relink updated. itemType={item.GetType().FullName}, oldPath={currentPath}, newPath={newPath}");
            logger.Flush();
            return (TimelineItemRelinkResultKind.Success, "\u9078\u629e\u30a2\u30a4\u30c6\u30e0\u306e\u30d5\u30a1\u30a4\u30eb\u30d1\u30b9\u3092\u66f4\u65b0\u3057\u307e\u3057\u305f\u3002");
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Timeline item relink failed. itemType={item.GetType().FullName}, oldPath={currentPath}, newPath={newPath}");
            logger.Flush();
            return (TimelineItemRelinkResultKind.Warning, "\u30d5\u30a1\u30a4\u30eb\u30d1\u30b9\u306e\u66f4\u65b0\u306b\u5931\u6557\u3057\u307e\u3057\u305f\u3002\u30ed\u30b0\u3092\u78ba\u8a8d\u3057\u3066\u304f\u3060\u3055\u3044\u3002");
        }
    }

    private static string BuildFilter(IItem item)
    {
        if (item is ImageItem)
        {
            return "\u753b\u50cf\u30d5\u30a1\u30a4\u30eb|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|\u3059\u3079\u3066\u306e\u30d5\u30a1\u30a4\u30eb|*.*";
        }

        if (item is VideoItem)
        {
            return "\u52d5\u753b\u30d5\u30a1\u30a4\u30eb|*.mp4;*.mov;*.avi;*.mkv;*.wmv;*.webm;*.m4v;*.mpg;*.mpeg|\u3059\u3079\u3066\u306e\u30d5\u30a1\u30a4\u30eb|*.*";
        }

        if (item is AudioItem)
        {
            return "\u97f3\u58f0\u30d5\u30a1\u30a4\u30eb|*.wav;*.mp3;*.aac;*.m4a;*.flac;*.ogg;*.wma|\u3059\u3079\u3066\u306e\u30d5\u30a1\u30a4\u30eb|*.*";
        }

        return "\u3059\u3079\u3066\u306e\u30d5\u30a1\u30a4\u30eb|*.*";
    }

    private static string? ResolveInitialDirectory(string currentPath)
    {
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            var currentDirectory = Path.GetDirectoryName(currentPath);
            if (!string.IsNullOrWhiteSpace(currentDirectory) && Directory.Exists(currentDirectory))
            {
                return currentDirectory;
            }
        }

        var projectPath = YmmProjectPathResolver.TryGetCurrentProjectPath();
        var projectDirectory = string.IsNullOrWhiteSpace(projectPath) ? null : Path.GetDirectoryName(projectPath);
        if (!string.IsNullOrWhiteSpace(projectDirectory) && Directory.Exists(projectDirectory))
        {
            return projectDirectory;
        }

        return null;
    }
}
