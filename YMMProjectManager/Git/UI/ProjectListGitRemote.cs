using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using YMMProjectManager.Infrastructure.Git;

namespace YMMProjectManager.Presentation.ViewModels;

//CSJ,class=ProjectListViewModel,プロジェクト一覧の表示モデル
public sealed partial class ProjectListViewModel
{
    //CSJ,method=ShowGitDetailsAsync,Git詳細を表示する（非同期）
    private async Task ShowGitDetailsAsync(bool remote)
    {
        var selected = SelectedProject;
        if (selected is null) return;
        await ExecuteToolAsync("Git", async token =>
        {
            var located = await LocateProjectGitRootAsync(selected, token);
            if (located is null) { GitStatus = "プロジェクトの Git ルートを確認できません。"; return; }
            var git = located.Value.Repository;
            var result = remote ? await git.InspectRemoteAsync(GitRemoteOperation.Fetch, token) : await git.HistoryAsync(token);
            if (!ReferenceEquals(selected, SelectedProject)) return;
            if (!result.Success) { GitStatus = "情報を取得できません。origin またはコミット履歴を確認してください。"; return; }
            if (remote)
            {
                if (result.FetchTarget is not { } target) { GitStatus = "origin の接続先を確認できません。"; return; }
                //CSJ,variable=label,ラベル
                var label = GitRepository.IsSupportedGitHubEndpoint(target.Endpoint) ? "GitHub origin" : "origin";
                GitStatus = $"{label}\n{target.Endpoint}";
                return;
            }
            GitStatus = result.StandardOutput.Replace('\0', ' ');
        });
    }

    //CSJ,method=CloneGitAsync,Gitをクローンする（非同期）
    private async Task CloneGitAsync()
    {
        //CSJ,variable=remote,リモート
        var remote = GitRemote;
        //CSJ,variable=name,名前
        var name = GitCloneName;
        if (string.IsNullOrWhiteSpace(remote) || string.IsNullOrWhiteSpace(name) || name is "." or ".." || Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        { GitStatus = "HTTPS またはローカルの URL と、フォルダー名を入力してください。"; return; }
        var inspected = GitRepository.InspectCloneRemote(remote);
        if (!inspected.Success) { GitStatus = DescribeRemoteFailure(inspected); return; }
        //CSJ,variable=dialog,ダイアログ
        var dialog = new OpenFolderDialog { Title = "clone 先の親フォルダーを選択" };
        if (dialog.ShowDialog() != true) return;
        //CSJ,variable=destination,格納先
        var destination = Path.Combine(dialog.FolderName, name);
        //CSJ,variable=authentication,認証
        var authentication = AuthenticationFor(remote);
        if (!ConfirmGitTrust("Clone", destination, inspected.StandardOutput + AuthenticationNotice(authentication))) return;
        await ExecuteToolAsync("Git clone", async token =>
        {
            GitStatus = "Clone を実行中です。";
            try
            {
                var result = await GitRepository.CloneTrustedAsync(remote, destination, authentication, token);
                GitStatus = result.Success ? $"Clone が完了しました。{destination}" :
                    DescribeRemoteFailure(result, GitRepository.IsSupportedGitHubEndpoint(remote), authentication);
            }
            catch (ArgumentException)
            {
                GitStatus = "接続先または保存先の形式を確認してください。資格情報は URL に入力できません。";
            }
        });
    }

    //CSJ,method=RunTrustedRemoteAsync,信頼済みリモートを実行する（非同期）
    private Task RunTrustedRemoteAsync(GitRemoteOperation operation) => ExecuteToolAsync($"Git {operation}", async token =>
    {
        //CSJ,variable=project,プロジェクト
        var project = SelectedProject;
        if (project is null) return;
        var located = await LocateProjectGitRootAsync(project, token);
        if (located is null) { GitStatus = "このプロジェクトの Git ルートを確認できません。"; return; }
        var (git, directory) = located.Value;
        var inspected = await git.InspectRemoteAsync(operation, token);
        if (!inspected.Success) { GitStatus = DescribeRemoteFailure(inspected); return; }
        //CSJ,variable=push,プッシュ
        var push = inspected.PushTarget;
        //CSJ,variable=fetch,フェッチ
        var fetch = inspected.FetchTarget;
        if (push is null && fetch is null) throw new InvalidDataException("承認対象のリポジトリと接続先を確認できません。");
        //CSJ,variable=details,詳細
        var details = push is null ? fetch!.Endpoint :
            $"{push.Endpoint}\n対象 {push.BranchRef}\nコミット {push.ObjectId}";
        if (push is not null && Path.IsPathFullyQualified(push.Endpoint))
            details += "\nローカルの送信先では受信側Gitの設定と受信hookが適用されます。管理している送信先だけを承認してください。";
        //CSJ,variable=authentication,認証
        var authentication = AuthenticationFor(push?.Endpoint ?? fetch!.Endpoint);
        if (!ReferenceEquals(project, SelectedProject) || !ConfirmGitTrust(operation.ToString(), directory,
                details + AuthenticationNotice(authentication))) return;
        token.ThrowIfCancellationRequested();
        GitStatus = $"{operation} を実行中です。";
        if (push is not null)
        {
            try { await RegisterKnownPublicationAssetsAsync(git, project, token); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException)
            {
                GitStatus = "プロジェクトまたは素材メタデータを確認できないため、Pushを中止しました。";
                return;
            }
        }
        var result = push is null
            ? await git.FetchTrustedAsync(fetch!, authentication, token)
            : await git.PushTrustedAsync(push, authentication, token);
        GitStatus = result.Success ? $"{operation} が完了しました。" :
            DescribeRemoteFailure(result, GitRepository.IsSupportedGitHubEndpoint(push?.Endpoint ?? fetch!.Endpoint), authentication);
    });

    private static bool ConfirmGitTrust(string operation, string directory, string endpoint) =>
        MessageBox.Show($"{operation}\n{directory}\n{endpoint}\n\nインストール済みの Git、システム・利用者・リポジトリの Git 設定、認証ヘルパーなどを信頼する場合に限り続行してください。",
            "Git 操作の確認", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    //CSJ,method=DescribeRemoteFailure,リモート失敗を説明する
    internal static string DescribeRemoteFailure(GitResult result, bool isGitHub = false,
        //CSJ,parameter=authentication,認証
        GitHubAuthentication authentication = GitHubAuthentication.Anonymous)
    {
        //CSJ,variable=action,アクション
        var action = result.Failure switch
        {
            GitFailure.Authentication when isGitHub && authentication == GitHubAuthentication.Anonymous =>
                "匿名接続では認証できませんでした。アクセス権のある保存済み資格情報があれば「GitHubにWindowsの保存済み資格情報を使う」を有効にし、操作を承認し直してください。",
            GitFailure.Authentication when isGitHub =>
                "保存済み資格情報で認証できませんでした。アカウントと有効期限を確認してください。対応するGCMがあれば、GitHubのブラウザ再ログインを実行できます。",
            GitFailure.Authentication => "認証の有効期限、使用するアカウント、認証ヘルパーを確認してください。",
            GitFailure.Permission => "接続先と、対象リポジトリへの権限を確認してください。",
            GitFailure.Network => "接続先、ネットワーク、TLS 設定を確認してください。",
            GitFailure.RemoteRedirect => "接続先がリダイレクトを要求したため停止しました。正式なURLを確認し、接続先を承認し直してください。",
            GitFailure.Divergence => "送信先の履歴が変更されています。Fetch してブランチの状態を確認してください。",
            GitFailure.Timeout => "制限時間に達しました。接続状況と操作結果を確認してください。",
            GitFailure.PathTooLong => "より短い親フォルダーを選んでください。",
            GitFailure.MediaPublication => "素材・パッケージ・ローカル専用の回復記録が含まれるか、送信内容を検証できないため拒否しました。過去のコミットも確認してください。",
            GitFailure.UnsafeOperation => "接続先・ブランチ・リポジトリの状態または Git 設定を確認し、再度承認してください。",
            _ => "接続先、Git 設定またはブランチを確認してください。",
        };
        //CSJ,variable=message,メッセージ
        var message = $"Git 操作を完了できませんでした。{action}";
        return result.RecoveryPath is { } path ? $"{message}\n残存フォルダー {path}" : message;
    }
}
