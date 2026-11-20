using System.Windows;
using System.Windows.Controls;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectListViewModel
{
    internal sealed record PullApprovalDetails(string RepositoryRoot, string BranchRef, string RemoteName,
        string UpstreamRef, string Endpoint, string StartObjectId, string TargetObjectId, bool ChangesWorktree);

    internal static string DescribePullApproval(PullApprovalDetails target)
    {
        var change = target.ChangesWorktree
            ? "対象ブランチ、index、作業ファイルを更新します。"
            : "現在と更新先のコミットは同じです。作業ファイルは更新しません。";
        return $"""
            リポジトリ {target.RepositoryRoot}
            現在のブランチ {target.BranchRef}
            接続先 {target.RemoteName} / {target.UpstreamRef}
            URL {target.Endpoint}
            現在のコミット {target.StartObjectId}
            更新先のコミット {target.TargetObjectId}

            Fast-forward のみ実行します。未コミット変更や危険な競合が検出された場合は実行しません。
            {change}
            実行中は、他の Git ツールやエディタで同じリポジトリを変更しないでください。
            外部アプリによる同時変更を完全には防止できません。
            """;
    }

    private static bool ConfirmPull(PullApprovalDetails target)
    {
        var cancel = new Button { Content = "キャンセル", IsCancel = true, IsDefault = true, MinWidth = 120 };
        var run = new Button { Content = "Pull を実行", MinWidth = 120, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(run);
        var text = new TextBlock { Text = DescribePullApproval(target), TextWrapping = TextWrapping.Wrap };
        var panel = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var window = new Window
        {
            Title = "Git Pull を実行しますか？",
            Owner = GetActiveWindow(),
            Width = 680,
            Height = 420,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        window.Loaded += (_, _) => cancel.Focus();
        run.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true;
    }

    internal Task RunGitPullWorkflowAsync<TRemote, TTarget>(
        Func<ProjectEntry, CancellationToken, Task<TRemote?>> inspectRemote,
        Func<TRemote, bool> confirmTransport,
        Func<TRemote, CancellationToken, Task<TTarget?>> inspectTarget,
        Func<TTarget, bool> confirmTarget,
        Func<TTarget, CancellationToken, Task<string>> execute)
        where TRemote : class
        where TTarget : class
        => ExecuteToolAsync("Git Pull", async token =>
        {
            if (SelectedProject is not { } project) return;
            var projectPath = project.FullPath;

            bool StillSelected()
            {
                token.ThrowIfCancellationRequested();
                if (ReferenceEquals(project, SelectedProject) && project.FullPath == projectPath) return true;
                GitStatus = "選択中のプロジェクトが変更されたため Pull を中止しました。新しい対象を確認してください。";
                return false;
            }

            GitStatus = "Pull の接続先を確認中です。";
            if (!StillSelected()) return;
            var remote = await inspectRemote(project, token);
            if (!StillSelected() || remote is null) return;
            if (!confirmTransport(remote))
            {
                GitStatus = "接続先の確認を取り消しました。";
                return;
            }

            GitStatus = "リモートの更新先コミットを確認中です。";
            if (!StillSelected()) return;
            var target = await inspectTarget(remote, token);
            if (!StillSelected() || target is null) return;
            if (!confirmTarget(target))
            {
                GitStatus = "Pull を取り消しました。作業ツリーは更新していません。";
                return;
            }

            GitStatus = "Pull を実行中です。操作結果を確認するまでお待ちください。";
            if (!StillSelected()) return;
            GitStatus = await execute(target, token);
        });
}
