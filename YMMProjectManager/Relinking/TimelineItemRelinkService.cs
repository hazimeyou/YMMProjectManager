using YukkuriMovieMaker.Plugin;

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
        logger.Info("Timeline item relink blocked until host undo and recovery are verified.");
        return (TimelineItemRelinkResultKind.Warning,
            "タイムラインへの反映は安全性を確認できるまで停止しています。保存済みプロジェクトを別名保存する再リンクを使用してください。");
    }
}
