using System.Windows.Input;
using YMMProjectManager.Infrastructure.Git;

namespace YMMProjectManager.Presentation.ViewModels;

//CSJ,class=ProjectListViewModel,プロジェクト一覧の表示モデル
public sealed partial class ProjectListViewModel
{
    //CSJ,property=InspectGitHubAccessCommand,点検GitHubアクセスコマンド
    public ICommand InspectGitHubAccessCommand { get; }

    private bool useCachedGitHubAuthentication;

    //CSJ,property=UseCachedGitHubAuthentication,キャッシュ済みのGitHub認証を使用する
    public bool UseCachedGitHubAuthentication
    {
        get => useCachedGitHubAuthentication;
        set => SetProperty(ref useCachedGitHubAuthentication, value);
    }

    private GitHubAuthentication AuthenticationFor(string endpoint) =>
        UseCachedGitHubAuthentication && GitRepository.IsSupportedGitHubEndpoint(endpoint)
            ? GitHubAuthentication.CachedWindows : GitHubAuthentication.Anonymous;

    private static string AuthenticationNotice(GitHubAuthentication authentication) =>
        authentication == GitHubAuthentication.CachedWindows
            ? "\nGitHubへの接続に、Windowsに保存済みのGit Credential Manager資格情報を使用します。"
            : "";

    //CSJ,field=GitHubAccessOperation,GitHubアクセス操作
    private const string GitHubAccessOperation = "GitHub access";

    //CSJ,method=InspectGitHubAccessAsync,GitHubアクセスを点検する（非同期）
    private Task InspectGitHubAccessAsync() => InspectGitHubAccessAsync(
        (root, summary) => ConfirmGitTrust("GitHubのアクセスを確認", root, summary),
        (repository, target, authentication, token) => repository.ProbeGitHubAccessAsync(target, authentication, token));

    //CSJ,method=InspectGitHubAccessAsync,GitHubアクセスを点検する（非同期）
    internal Task InspectGitHubAccessAsync(Func<string, string, bool> approve,
        //CSJ,parameter=probe,調査
        Func<GitRepository, GitFetchTarget, GitHubAuthentication, CancellationToken, Task<GitResult>> probe) =>
        ExecuteToolAsync(GitHubAccessOperation, async token =>
    {
        //CSJ,variable=project,プロジェクト
        var project = SelectedProject;
        if (project is null) return;
        //CSJ,variable=projectPath,プロジェクトのパス
        var projectPath = project.FullPath;
        var located = await LocateProjectGitRootAsync(project, token);
        if (located is null) { GitStatus = "Gitリポジトリを確認できません。"; return; }
        var (git, root) = located.Value;
        var origin = await git.InspectRemoteAsync(GitRemoteOperation.Fetch, token);
        if (!origin.Success || origin.FetchTarget is not { } target || !GitRepository.IsSupportedGitHubEndpoint(target.Endpoint))
        {
            GitStatus = "このリポジトリのoriginに https://github.com/所有者/リポジトリ を指定してください。";
            return;
        }
        if (!ReferenceEquals(project, SelectedProject) || projectPath != project.FullPath) return;
        //CSJ,variable=authentication,認証
        var authentication = AuthenticationFor(target.Endpoint);
        if (!approve(root, target.Endpoint + AuthenticationNotice(authentication)))
        {
            GitStatus = "GitHubへの接続を取り消しました。";
            return;
        }
        if (!ReferenceEquals(project, SelectedProject) || projectPath != project.FullPath)
        {
            GitStatus = "選択中のプロジェクトが変わりました。接続先を確認し直してください。";
            return;
        }
        token.ThrowIfCancellationRequested();
        GitStatus = "GitHubへのアクセスを確認しています。";
        var result = await probe(git, target, authentication, token);
        if (!ReferenceEquals(project, SelectedProject) || projectPath != project.FullPath) return;
        GitStatus = result.Success
            ? "対象リポジトリを読み取れます。公開リポジトリでは、応答だけでログイン済みかどうかを判定できません。"
            : DescribeRemoteFailure(result, isGitHub: true, authentication: authentication);
    });
}
