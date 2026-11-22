using System;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Presentation.ViewModels;
using YMMProjectManager.Presentation.Views;
using YukkuriMovieMaker.Plugin;

namespace YMMProjectManager.Plugin;

//CSJ,class=ToolPluginEntry,プロジェクト管理ツールの入口
public sealed class ToolPluginEntry : IToolPlugin
{
    //CSJ,property=Name,名前
    public string Name => "プロジェクトマネージャー";
    //CSJ,property=ViewModelType,ビューモデル型
    public Type ViewModelType => typeof(ProjectListViewModel);
    //CSJ,property=ViewType,ビュー型
    public Type ViewType => typeof(ProjectListView);

    public ToolPluginEntry()
    {
        PluginDiagnostics.Start(ViewModelType);
    }
}
