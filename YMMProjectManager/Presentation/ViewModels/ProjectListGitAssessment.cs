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
        if (git is null) { GitStatus = "このプロジェクトの Git ルートを確認できません。"; return; }
        var result = await git.Value.Repository.InspectPullAsync(token);
        if (!ReferenceEquals(project, SelectedProject)) return;
        var description = result.State switch
        {
            GitPullState.FastForwardCandidate => "保存済みの追跡ブランチには fast-forward できる履歴があります。",
            GitPullState.UpToDate => "保存済みの追跡ブランチと同じコミットです。",
            GitPullState.LocalAhead => "ローカル側に先行するコミットがあります。",
            GitPullState.Diverged => "ローカルと追跡ブランチが分岐しています。",
            GitPullState.Dirty => "作業ツリーまたは index に変更があります。",
            GitPullState.TrackingUnavailable => "保存済みの追跡ブランチがありません。必要に応じて Fetch してください。",
            GitPullState.Changed => "確認中にリポジトリが変更されました。再確認してください。",
            _ => "状態を確認できません。通常のリポジトリのルートとブランチを確認してください。",
        };
        GitStatus = $"{description}\n{result.BranchRef} {result.LocalObjectId}\n{result.TrackingRef} {result.TrackingObjectId}\nローカルの追跡情報を確認しました。通信や Pull は実行していません。";
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
        var relative = Path.GetRelativePath(root, directory);
        if (Path.IsPathFullyQualified(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;
        return (new GitRepository(root), root);
    }
}
