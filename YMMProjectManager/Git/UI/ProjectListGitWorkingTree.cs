using System.IO;
using System.Text.Json;
using System.Windows;
using YMMProjectManager.Infrastructure.Git;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectListViewModel
{
    private enum WorkingTreeAction { Stage, Unstage, Commit, CreateBranch, SwitchBranch }

    private async Task ApplyWorkingTreeActionAsync(WorkingTreeAction operation)
    {
        var selected = SelectedProject;
        if (selected is null) return;
        var requestedPath = GitPath;
        var requestedMessage = GitCommitMessage;
        var requestedBranch = GitBranch;
        await ExecuteToolAsync("Git", async token =>
        {
            var located = await LocateProjectGitRootAsync(selected, token);
            if (located is null || !ReferenceEquals(selected, SelectedProject)) { GitStatus = "Gitリポジトリを確認できません。"; return; }
            var (git, directory) = located.Value;
            if ((operation == WorkingTreeAction.Stage || operation == WorkingTreeAction.Unstage) && string.IsNullOrWhiteSpace(requestedPath)) { GitStatus = "Gitルートからの相対パスを指定してください。"; return; }
            if (operation == WorkingTreeAction.Commit && string.IsNullOrWhiteSpace(requestedMessage)) { GitStatus = "コミットメッセージを入力してください。"; return; }
            if ((operation == WorkingTreeAction.CreateBranch || operation == WorkingTreeAction.SwitchBranch) && string.IsNullOrWhiteSpace(requestedBranch)) { GitStatus = "ブランチ名を入力してください。"; return; }
            if (operation == WorkingTreeAction.SwitchBranch)
            {
                if (MessageBox.Show("ブランチ切替時に、このリポジトリで設定された Git フィルターなどの外部プログラムが実行される場合があります。設定を信頼できる場合のみ続行してください。", "Git", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            }
            if (operation is WorkingTreeAction.Commit or WorkingTreeAction.Stage)
            {
                try { await RegisterKnownPublicationAssetsAsync(git, selected, token); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException)
                {
                    GitStatus = "プロジェクトまたは素材メタデータを確認できないため、Git操作を中止しました。";
                    return;
                }
            }
            GitCommitTarget? commitTarget = null;
            if (operation == WorkingTreeAction.Commit)
            {
                var inspected = await git.InspectCommitAsync(token);
                if (!inspected.Success || inspected.CommitTarget is not { } target) { GitStatus = DescribeCommitFailure(inspected); return; }
                if (!ReferenceEquals(selected, SelectedProject) || !ConfirmGitCommit(target, requestedMessage)) return;
                commitTarget = target;
            }
            if (!ReferenceEquals(selected, SelectedProject)) return;
            var result = operation switch
            {
                WorkingTreeAction.Stage => await git.StageAsync([requestedPath], token),
                WorkingTreeAction.Unstage => await git.UnstageAsync([requestedPath], token),
                WorkingTreeAction.Commit => await git.CommitAsync(commitTarget!, requestedMessage, token),
                WorkingTreeAction.CreateBranch => await git.CreateBranchAsync(requestedBranch, token),
                WorkingTreeAction.SwitchBranch => await git.SwitchBranchAsync(requestedBranch, token),
                _ => throw new InvalidOperationException("Unknown Git operation."),
            };
            if (!ReferenceEquals(selected, SelectedProject)) return;
            if (!result.Success) { GitStatus = DescribeCommitFailure(result); return; }
            GitStatus = operation == WorkingTreeAction.Commit ? $"コミットしました。\n{result.StandardOutput.Trim()}" : "Git 操作が完了しました。";
            if (operation == WorkingTreeAction.Commit && GitCommitMessage == requestedMessage) GitCommitMessage = "";
            try
            {
                var status = await git.StatusAsync(token);
                if (status.Success && ReferenceEquals(selected, SelectedProject)) UpdateGitChanges(status.StandardOutput);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The mutation already succeeded. Cancellation affects only the display refresh.
            }
        });
    }

    private async Task ShowGitStatusAsync(bool initialize)
    {
        var selected = SelectedProject;
        if (selected is null) return;
        await ExecuteToolAsync("Git", async token =>
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(selected.FullPath));
            if (directory is null || !Directory.Exists(directory))
            {
                GitStatus = "プロジェクトのフォルダーが見つかりません。";
                return;
            }
            var git = new GitRepository(directory);
            var detected = await git.DetectAsync(token);
            if (!ReferenceEquals(selected, SelectedProject)) return;
            if (!detected.Success && initialize)
            {
                if (!detected.StandardError.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
                {
                    GitStatus = "Git リポジトリの状態を確認できないため、初期化を中止しました。";
                    return;
                }
                if (MessageBox.Show("このフォルダーに Git リポジトリを初期化しますか？", "Git", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                var recheck = await git.DetectAsync(token);
                if (recheck.Success || !recheck.StandardError.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
                {
                    GitStatus = "Git の状態が変化したため、初期化を中止しました。";
                    return;
                }
                var result = await git.InitializeAsync(token);
                if (!result.Success) { GitStatus = "Git の初期化に失敗しました。"; return; }
                detected = await git.DetectAsync(token);
            }
            if (!detected.Success) { GitStatus = "Git リポジトリが見つかりません。"; return; }
            var status = await git.StatusAsync(token);
            var branches = await git.BranchesAsync(token);
            if (!ReferenceEquals(selected, SelectedProject)) return;
            if (status.Success) UpdateGitChanges(status.StandardOutput);
            GitStatus = status.Success && branches.Success
                ? $"Git: {detected.StandardOutput.Trim()}\n{branches.StandardOutput.Trim()}\n{(GitChanges.Count == 0 ? "変更なし" : $"変更 {GitChanges.Count} 件")}"
                : "Git の状態を取得できませんでした。";
        });
    }
}
