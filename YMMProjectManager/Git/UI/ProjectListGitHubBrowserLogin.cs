using System.Windows;
using System.Windows.Input;
using YMMProjectManager.Infrastructure.Git;

namespace YMMProjectManager.Presentation.ViewModels;

//CSJ,class=ProjectListViewModel,プロジェクト一覧の表示モデル
public sealed partial class ProjectListViewModel
{
    //CSJ,field=GitHubBrowserLoginOperation,GitHubブラウザーログイン操作
    private const string GitHubBrowserLoginOperation = "GitHub browser login";

    //CSJ,property=GitHubBrowserLoginCommand,GitHubブラウザーログインコマンド
    public ICommand GitHubBrowserLoginCommand { get; }
    //CSJ,property=GitHubBrowserReloginCommand,ブラウザーでのGitHub再ログインコマンド
    public ICommand GitHubBrowserReloginCommand { get; }
    //CSJ,property=CancelGitHubBrowserLoginCommand,キャンセルGitHubブラウザーログインコマンド
    public ICommand CancelGitHubBrowserLoginCommand { get; }

    //CSJ,method=RunGitHubBrowserLoginAsync,GitHubブラウザーログインを実行する（非同期）
    private Task RunGitHubBrowserLoginAsync(bool reauthenticate) =>
        RunGitHubBrowserLoginAsync(reauthenticate, ConfirmGitHubBrowserLogin,
            GitRepository.GetGitHubBrowserLoginAvailabilityAsync, GitRepository.SignInGitHubInBrowserAsync);

    //CSJ,method=RunGitHubBrowserLoginAsync,GitHubブラウザーログインを実行する（非同期）
    internal Task RunGitHubBrowserLoginAsync(bool reauthenticate, Func<bool, bool> approve,
        Func<CancellationToken, Task<bool>> checkAvailability,
        Func<bool, CancellationToken, Task<GitResult>> signIn) =>
        ExecuteToolAsync(GitHubBrowserLoginOperation, async token =>
        {
            GitStatus = "GitHubブラウザ認証に対応するGCMを確認しています。";
            if (!await checkAvailability(token))
            {
                token.ThrowIfCancellationRequested();
                GitStatus = "GitHubブラウザ認証に対応する、検証済みのGCM 3.0.2（Windows x64）が見つかりません。公式版のシステムインストールを確認してください。";
                return;
            }
            token.ThrowIfCancellationRequested();
            if (!approve(reauthenticate))
            {
                GitStatus = "GitHubブラウザ認証を取り消しました。";
                return;
            }
            token.ThrowIfCancellationRequested();
            GitStatus = "既定のブラウザでGitHub認証を完了してください。待機を中止するには「認証を中止」を押してください。";
            var result = await signIn(reauthenticate, token);
            GitStatus = DescribeGitHubBrowserLoginResult(result, reauthenticate);
        });

    //CSJ,method=ConfirmGitHubBrowserLogin,GitHubブラウザーログインを確認する
    private static bool ConfirmGitHubBrowserLogin(bool reauthenticate)
    {
        //CSJ,variable=action,アクション
        var action = reauthenticate
            ? "再ログインすると、Git Credential Manager が Windows に保存する資格情報が更新される場合があります。"
            : "Git Credential Manager が既定のブラウザを使い、資格情報を Windows に保存します。既にブラウザでログインしている場合は、そのセッションが再利用されることがあります。";
        return MessageBox.Show(
            $"GitHub.com のアカウント認証を開始します。\n\n{action}\n\nClone・Fetch・Pull・Push は接続先を確認し、別途承認してください。",
            reauthenticate ? "GitHub ブラウザ再ログイン" : "GitHub ブラウザログイン",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    //CSJ,method=DescribeGitHubBrowserLoginResult,GitHubブラウザーログイン結果を説明する
    internal static string DescribeGitHubBrowserLoginResult(GitResult result, bool reauthenticate)
    {
        if (result.Success)
        {
            //CSJ,variable=operation,操作
            var operation = reauthenticate ? "再ログイン" : "ログイン";
            return $"GCM の GitHub {operation}が完了しました。保存済み資格情報を使うにはチェックを入れ、対象リポジトリへのアクセスを確認してください。複数の資格情報がある場合は、使用されるアカウントを確認してください。";
        }

        return result.Failure switch
        {
            GitFailure.OutcomeUnknown => "GitHub認証の結果を確認できません。Windowsの保存済み資格情報が更新された可能性があります。再試行する前に、使用するアカウントと対象リポジトリへのアクセスを確認してください。",
            GitFailure.ConcurrentOperation => "別のGitHub認証が進行中です。完了してから再試行してください。",
            GitFailure.Timeout => "GitHub認証の待機時間を超えました。Windowsの保存済み資格情報が更新された可能性があります。対象リポジトリへのアクセスを確認してから再試行してください。",
            GitFailure.UnsafeOperation => "GitHubブラウザ認証を開始できません。対応するGCMのインストールと検証状態を確認してください。",
            _ => "GitHubブラウザ認証に失敗しました。既定のブラウザ、ネットワーク、使用するGitHubアカウントを確認してください。",
        };
    }
}
