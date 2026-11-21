using System.Windows;
using System.Windows.Controls;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure.Git;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectListViewModel
{
    private const string PullWorkflowOperation = "Git Pull";

    public string PullHelp =>
        "承認後に fast-forward のみ実行します。作業ツリーには固定ローカルNTFSが必要です。GitHubでは保存済みのWindows資格情報を選べます。";

    public string GitRemoteOperationsHelp =>
        "Clone・Fetch・Push は操作ごとに信頼を確認します。Pull は接続の承認と更新先の確認が必要です。";

    internal sealed record PullApprovalDetails(string RepositoryRoot, string BranchRef, string RemoteName,
        string UpstreamRef, string Endpoint, string StartObjectId, string TargetObjectId, bool ChangesWorktree);

    private Task RunConfirmedGitPullAsync() =>
        RunConfirmedGitPullAsync(
            (root, remote) => ConfirmGitTrust("更新先コミットを確認するためリモートへ接続します。Pull の実行は次の画面で選べます。", root, remote),
            ConfirmPull, UseCachedGitHubAuthentication);

    internal Task RunConfirmedGitPullAsync(Func<string, string, bool> approveTransport, Func<PullApprovalDetails, bool> approveTarget,
        bool useCachedGitHubAuthentication = false)
        => RunGitPullWorkflowAsync<GitPullRemote, GitPullTarget>(
            async (project, token) =>
            {
                var located = await LocateProjectGitRootAsync(project, token);
                if (located is null)
                {
                    GitStatus = "このプロジェクトの Git ルートを確認できません。";
                    return null;
                }
                var result = await located.Value.Repository.InspectPullRemoteAsync(token);
                if (useCachedGitHubAuthentication && result.PullRemote is { } anonymous &&
                    GitRepository.IsSupportedGitHubEndpoint(anonymous.Endpoint))
                    result = await located.Value.Repository.InspectPullRemoteAsync(GitHubAuthentication.CachedWindows, token);
                if (result.Success && result.PullRemote is { } remote &&
                    string.Equals(located.Value.Root, remote.RepositoryRoot, StringComparison.OrdinalIgnoreCase))
                    return remote;
                GitStatus = DescribePullFailure(located.Value.Root, result);
                return null;
            },
            remote => approveTransport(remote.RepositoryRoot, $"{remote.RemoteName} / {remote.UpstreamRef}\n{remote.Endpoint}" +
                AuthenticationNotice(remote.Authentication)),
            async (remote, token) =>
            {
                var result = await new GitRepository(remote.RepositoryRoot).InspectPullTargetAsync(remote, token);
                if (result.Success && result.PullTarget is { } target &&
                    string.Equals(remote.RepositoryRoot, target.RepositoryRoot, StringComparison.OrdinalIgnoreCase) &&
                    remote.BranchRef == target.BranchRef && remote.StartObjectId == target.StartObjectId &&
                    remote.RemoteName == target.RemoteName && remote.UpstreamRef == target.UpstreamRef &&
                    remote.Endpoint == target.Endpoint && remote.Identity == target.Identity &&
                    remote.Authentication == target.Remote.Authentication)
                    return target;
                GitStatus = DescribePullFailure(remote.RepositoryRoot, result, remote: remote);
                return null;
            },
            target => approveTarget(new PullApprovalDetails(target.RepositoryRoot, target.BranchRef,
                target.RemoteName, target.UpstreamRef, target.Endpoint, target.StartObjectId, target.TargetObjectId,
                target.ChangesWorktree)),
            async (target, token) =>
            {
                var result = await new GitRepository(target.RepositoryRoot).PullTrustedAsync(target, token);
                logger.Info($"Pull outcome: exit={result.ExitCode}; failure={result.Failure}; elapsed={result.RemoteDiagnostic?.Elapsed}");
                if (!result.Success) return DescribePullFailure(target.RepositoryRoot, result, target.BranchRef, target.Remote);
                var action = target.ChangesWorktree
                    ? "Pull が完了しました。ブランチ、index、作業ファイルを確認しました。"
                    : "最新です。作業ファイルの更新はありません。";
                return $"{action}\n対象リポジトリ {target.RepositoryRoot}\n現在のブランチ {target.BranchRef}\n確認したコミット {target.TargetObjectId}";
            });

    internal static string DescribePullFailure(string repository, GitResult result, string? branch = null, GitPullRemote? remote = null)
    {
        var action = result.Failure switch
        {
            GitFailure.OutcomeUnknown => "Pull の結果を確定できません。作業ファイルと回復記録を保全し、Git の状態、現在のブランチ、HEAD、index を確認してください。確認が済むまで再実行や reset は行わないでください。回復記録が残っている間は Pull を開始できません。",
            GitFailure.ConcurrentOperation => "別の Git 操作が進行中です。その操作が終了してから、状態を確認してください。",
            GitFailure.Divergence => "履歴が分岐しているため fast-forward できません。ローカルと追跡先の履歴を確認してください。",
            GitFailure.UnsafeOperation => "現在の状態では Pull できません。ブランチ、上流、未コミット・未追跡ファイル、Git 設定を確認してください。",
            _ when result.Success => "承認対象が変わったため Pull を停止しました。接続先から確認し直してください。",
            _ => DescribeRemoteFailure(result with { RecoveryPath = null },
                remote is not null && GitRepository.IsSupportedGitHubEndpoint(remote.Endpoint),
                remote?.Authentication ?? GitHubAuthentication.Anonymous),
        };
        var affected = branch is null ? repository : $"{repository}\n対象ブランチ {branch}";
        var evidence = result.RecoveryPath is { } path ? $"\n回復記録 {path}" : string.Empty;
        return $"{action}\n対象リポジトリ {affected}{evidence}";
    }

    internal static string DescribePullApproval(PullApprovalDetails target)
    {
        var change = target.ChangesWorktree
            ? "対象ブランチを更新します。差分がある場合は index と作業ファイルを更新します。"
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

    private static bool ConfirmPull(PullApprovalDetails target) => CreatePullConfirmation(target).ShowDialog() == true;

    internal static Window CreatePullConfirmation(PullApprovalDetails target)
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
        return window;
    }

    internal Task RunGitPullWorkflowAsync<TRemote, TTarget>(
        Func<ProjectEntry, CancellationToken, Task<TRemote?>> inspectRemote,
        Func<TRemote, bool> confirmTransport,
        Func<TRemote, CancellationToken, Task<TTarget?>> inspectTarget,
        Func<TTarget, bool> confirmTarget,
        Func<TTarget, CancellationToken, Task<string>> execute)
        where TRemote : class
        where TTarget : class
        => ExecuteToolAsync(PullWorkflowOperation, async token =>
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

            GitStatus = "Pull を実行中です。作業ツリーの更新開始後は、結果を確認するまで待機します。";
            if (!StillSelected()) return;
            GitStatus = await execute(target, token);
        });
}
