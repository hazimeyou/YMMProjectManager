using System.Windows.Input;
using YMMProjectManager.Infrastructure.Git;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectListViewModel
{
    public ICommand InspectGitHubAccessCommand { get; }

    private bool useCachedGitHubAuthentication;

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

    private const string GitHubAccessOperation = "GitHub access";

    private Task InspectGitHubAccessAsync() => InspectGitHubAccessAsync(
        (root, summary) => ConfirmGitTrust("GitHubのアクセスを確認", root, summary),
        (repository, target, authentication, token) => repository.ProbeGitHubAccessAsync(target, authentication, token));

    internal Task InspectGitHubAccessAsync(Func<string, string, bool> approve,
        Func<GitRepository, GitFetchTarget, GitHubAuthentication, CancellationToken, Task<GitResult>> probe) =>
        ExecuteToolAsync(GitHubAccessOperation, async token =>
    {
        var project = SelectedProject;
        if (project is null) return;
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
