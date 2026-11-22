using YukkuriMovieMaker.Plugin;

namespace YMMProjectManager.Infrastructure;

//CSJ,enum=TimelineItemRelinkResultKind,タイムライン項目再リンク結果種別
public enum TimelineItemRelinkResultKind
{
    //CSJ,field=Success,成功
    Success,
    //CSJ,field=Info,情報
    Info,
    //CSJ,field=Warning,警告
    Warning,
}

//CSJ,class=TimelineItemRelinkService,タイムライン項目再リンクサービス
public sealed class TimelineItemRelinkService
{
    //CSJ,field=logger,ロガー
    private readonly FileLogger logger;

    public TimelineItemRelinkService(FileLogger logger)
    {
        this.logger = logger;
    }

    //CSJ,method=ReplaceSelectedItemFilePath,選択中の項目ファイルパスを置換する
    public (TimelineItemRelinkResultKind Kind, string Message) ReplaceSelectedItemFilePath(TimelineToolInfo? info)
    {
        logger.Info("Timeline item relink blocked until host undo and recovery are verified.");
        return (TimelineItemRelinkResultKind.Warning,
            "タイムラインへの反映は安全性を確認できるまで停止しています。保存済みプロジェクトを別名保存する再リンクを使用してください。");
    }
}
