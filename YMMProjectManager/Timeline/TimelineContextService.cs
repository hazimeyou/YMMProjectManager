using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;

namespace YMMProjectManager.Infrastructure;

//CSJ,class=TimelineContextService,タイムラインコンテキストサービス
public static class TimelineContextService
{
    //CSJ,property=Info,情報
    public static TimelineToolInfo? Info { get; set; }
    //CSJ,property=Timeline,タイムライン
    public static Timeline? Timeline => Info?.Timeline;
}
