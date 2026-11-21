using System.IO;
using System.Windows.Input;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure.Git;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectListViewModel
{
    public ICommand InspectGitPullCommand { get; }

    private Task InspectGitPullAsync() => ExecuteToolAsync("Git pull assessment", async token =>
    {
        var project = SelectedProject;
        if (project is null) return;
        var git = await LocateProjectGitRootAsync(project, token);
        if (!ReferenceEquals(project, SelectedProject)) return;
        if (git is null)
        {
            GitStatus = $"このプロジェクトの Git ルートを確認できません。\n{PullHelp}";
            return;
        }
        var result = await git.Value.Repository.InspectPullAsync(token);
        if (!ReferenceEquals(project, SelectedProject)) return;
        var description = result.State switch
        {
            GitPullState.FastForwardCandidate => "保存済みの追跡ブランチには fast-forward できる履歴があります。",
            GitPullState.UpToDate => "保存済みの追跡ブランチと同じコミットです。",
            GitPullState.LocalAhead => "ローカル側に先行するコミットがあります。",
            GitPullState.Diverged => "ローカルと追跡ブランチが分岐しています。",
            GitPullState.OperationInProgress => "別の Git 操作が進行中です。その操作を完了してから状態を確認してください。",
            GitPullState.IndexNotVerifiable => "現在の Git 設定または index では、作業ツリーが安全に更新可能か確認できません。",
            GitPullState.IgnoredPathCollision => "取り込む履歴のパスが、Git の追跡対象外として除外された作業ファイルと重なります。",
            GitPullState.Dirty => "作業ツリーまたは index に変更があります。",
            GitPullState.HistoryNotVerifiable => "Git の履歴を書き換えて表示する設定があるため、保存済みのブランチとの関係を確認できません。",
            GitPullState.TrackingUnavailable => "保存済みの追跡ブランチがありません。必要に応じて Fetch してください。",
            GitPullState.Changed => "確認中にリポジトリが変更されました。再確認してください。",
            _ => "状態を確認できません。通常のリポジトリのルートとブランチを確認してください。",
        };
        var lines = new List<string> { description, $"対象リポジトリ {git.Value.Root}" };
        if (result.BranchRef is { } branch)
            lines.Add($"現在のブランチ {branch} {result.LocalObjectId}".TrimEnd());
        if (result.TrackingRef is { } tracking)
            lines.Add($"保存済みの追跡 ref {tracking} {result.TrackingObjectId}".TrimEnd());
        lines.Add("ローカルの追跡情報を確認しました。通信は実行していません。");
        lines.Add(PullHelp);
        GitStatus = string.Join("\n", lines);
    });

    private static async Task<(GitRepository Repository, string Root)?> LocateProjectGitRootAsync(ProjectEntry project, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(project.FullPath));
        if (directory is null || !Directory.Exists(directory)) return null;
        var query = new GitRepository(directory);
        var rootResult = await query.DetectAsync(token);
        var root = rootResult.StandardOutput.TrimEnd('\r', '\n');
        if (!rootResult.Success || root.Contains('\n') || root.Contains('\r') || !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            return null;
        root = Path.GetFullPath(root);
        var relative = Path.GetRelativePath(root, directory);
        if (Path.IsPathFullyQualified(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;
        return (new GitRepository(root), root);
    }
}
