using System.ComponentModel;
using System.IO;

namespace YMMProjectManager.Infrastructure.Git;

//CSJ,enum=GitHubAuthentication,GitHub認証
public enum GitHubAuthentication
{
    Anonymous,
    //CSJ,field=CachedWindows,キャッシュ済みWindows
    CachedWindows,
}

//CSJ,class=GitRepository,Gitリポジトリ操作
public sealed partial class GitRepository
{
    //CSJ,method=CloneTrustedAsync,信頼済みをクローンする（非同期）
    public static Task<GitResult> CloneTrustedAsync(string remote, string destination, GitHubAuthentication authentication,
        CancellationToken token = default) =>
        WithinRemoteDeadlineAsync("Clone", deadline => CloneCoreAsync(remote, destination, null, deadline, trusted: true,
            authentication), token, Timeout);

    //CSJ,method=FetchTrustedAsync,信頼済みをフェッチする（非同期）
    public Task<GitResult> FetchTrustedAsync(GitFetchTarget target, GitHubAuthentication authentication, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return WithinRemoteDeadlineAsync("Fetch", deadline => WithAppWriteLeaseAsync(
            () => FetchCoreAsync(target.Endpoint, deadline, target, authentication), deadline), token, RemoteTimeout);
    }

    //CSJ,method=PushTrustedAsync,信頼済みをプッシュする（非同期）
    public Task<GitResult> PushTrustedAsync(GitPushTarget target, GitHubAuthentication authentication, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return WithinRemoteDeadlineAsync("Push", deadline => WithAppWriteLeaseAsync(
            () => PushCoreAsync(target.Endpoint, deadline, target, authentication), deadline), token, RemoteTimeout);
    }

    /// <summary>Checks access to one GitHub repository. A public repository can succeed anonymously.</summary>
    //CSJ,method=ProbeGitHubAccessAsync,GitHubアクセスを調査する（非同期）
    public Task<GitResult> ProbeGitHubAccessAsync(string endpoint, GitHubAuthentication authentication, CancellationToken token = default) =>
        ProbeGitHubAccessCoreAsync(endpoint, null, authentication, token);

    /// <summary>Checks the exact repository origin selected for the user's access approval.</summary>
    //CSJ,method=ProbeGitHubAccessAsync,GitHubアクセスを調査する（非同期）
    public Task<GitResult> ProbeGitHubAccessAsync(GitFetchTarget target, GitHubAuthentication authentication, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return ProbeGitHubAccessCoreAsync(target.Endpoint, target, authentication, token);
    }

    //CSJ,method=ProbeGitHubAccessCoreAsync,GitHubアクセス中核を調査する（非同期）
    private Task<GitResult> ProbeGitHubAccessCoreAsync(string endpoint, GitFetchTarget? approvedTarget,
        //CSJ,parameter=authentication,認証
        GitHubAuthentication authentication, CancellationToken token) =>
        WithinRemoteDeadlineAsync("GitHub access", async deadline =>
        {
            if (!IsSupportedGitHubEndpoint(endpoint) || !Enum.IsDefined(authentication))
                return UnsupportedGitHubEndpoint();
            if (approvedTarget is not null && !string.Equals(directory, approvedTarget.RepositoryRoot, StringComparison.OrdinalIgnoreCase))
                return UnsafeRemote("This access approval belongs to a different repository.");
            //CSJ,variable=session,セッション
            using var session = await OpenPullSessionAsync(deadline, requireIndex: false).ConfigureAwait(false);
            if (session is null) return UnsupportedGitHubRepository();
            if (approvedTarget is not null && session.Identity != approvedTarget.Identity)
                return UnsafeRemote("The approved repository or its Git directory changed. Review the access target again.");
            var result = await RunGitHubTransportAsync(endpoint, ["ls-remote", "--symref", "--", endpoint, "HEAD"],
                deadline, authentication, session.Settings, verifyApprovedOrigin: true).ConfigureAwait(false);
            return SanitizeRemote(result, classifyPushStatus: false, cloneStage: null);
        }, token, RemoteTimeout);

    //CSJ,method=HasGitHubHost,GitHubホストの有無を判定する
    private static bool HasGitHubHost(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        string.Equals(uri.IdnHost.TrimEnd('.'), "github.com", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSupportedGitHubEndpoint(string endpoint)
    {
        //CSJ,variable=prefix,接頭辞
        const string prefix = "https://github.com/";
        if (!endpoint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        //CSJ,variable=parts,構成要素
        var parts = endpoint[prefix.Length..].Split('/');
        return parts.Length == 2 && parts[0].Length is >= 1 and <= 39 && parts[1].Length is >= 1 and <= 104 &&
            parts[0].All(c => char.IsAsciiLetterOrDigit(c) || c == '-') &&
            parts[1].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') &&
            parts[1] is not ("." or "..") && !parts[1].Equals(".git", StringComparison.OrdinalIgnoreCase);
    }

    private static GitResult UnsupportedGitHubEndpoint() =>
        UnsafeRemote("Choose a literal https://github.com/owner/repository URL without credentials or aliases.");

    private static GitResult UnsupportedGitHubRepository() =>
        UnsafeRemote("GitHub requires a verified main repository without unsupported configuration.");

    private async Task<bool> VerifyLiteralTransportEndpointAsync(string endpoint, CancellationToken token)
    {
        //CSJ,variable=names,名前一覧
        var names = await RunDirectAsync(["config", "--local", "--no-includes", "--name-only", "-z", "--list"],
            token, pullSettings: [], outputLimit: 1024 * 1024).ConfigureAwait(false);
        return VerifiedRemoteNames(names, endpoint, rejectUnsupported: false);
    }

    //CSJ,method=VerifiedRemoteNames,検証済みリモート名前一覧
    private static bool VerifiedRemoteNames(GitResult names, string endpoint, bool rejectUnsupported) =>
        names.Success && names.StandardError.Length == 0 && !names.StandardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Any(name => name.StartsWith("remote." + endpoint + ".", StringComparison.OrdinalIgnoreCase) ||
                (rejectUnsupported && UnsupportedPullSetting(name)));

    //CSJ,method=MachineGitInstallation,端末Gitインストール
    private static string MachineGitInstallation() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git");

    //CSJ,method=RunGitHubTransportAsync,GitHub通信を実行する（非同期）
    private async Task<GitResult> RunGitHubTransportAsync(string endpoint, string[] arguments, CancellationToken token,
        //CSJ,parameter=authentication,認証
        GitHubAuthentication authentication, string[] settings, string? cloneStage = null, bool verifyApprovedOrigin = false)
    {
        if (!IsSupportedGitHubEndpoint(endpoint) || !Enum.IsDefined(authentication))
            return UnsupportedGitHubEndpoint();
        if (cloneStage is not null && directory.Contains(Path.PathSeparator))
            return UnsupportedGitHubRepository();
        //CSJ,variable=installation,インストール
        var installation = MachineGitInstallation();
        //CSJ,variable=native,ネイティブ
        var native = Path.Combine(installation, "mingw64", "bin");
        //CSJ,variable=executable,実行ファイル
        var executable = Path.Combine(native, "git.exe");
        //CSJ,variable=manager,管理
        var manager = Path.Combine(native, "git-credential-manager.exe");
        if (!Path.IsPathFullyQualified(installation) || HasHiddenText(installation) ||
            installation.Contains('\'') || installation.Contains(Path.PathSeparator))
            return UnsupportedGitHubInstallation();
        try
        {
            // Only the normal machine-managed installation is accepted. PATH and
            // repository/global helpers are outside this credential trust boundary.
            using var toolchain = OwnedFilePublication.DirectoryFence.Open(native, enforceShare: true);
            //CSJ,variable=gitFile,Gitファイル
            using var gitFile = ProjectListReplacement.Open(executable);
            //CSJ,variable=managerFile,管理ファイル
            using var managerFile = authentication == GitHubAuthentication.CachedWindows ? ProjectListReplacement.Open(manager) : null;
            //CSJ,variable=profile,プロファイル
            var profile = new GitHubTransportProfile(installation, executable, cloneStage is null ? null : directory,
                authentication == GitHubAuthentication.CachedWindows);
            //CSJ,variable=destination,格納先
            var destination = cloneStage is null
                ? new[] { $"--git-dir={Path.Combine(directory, ".git")}", $"--work-tree={directory}" }
                : new[] { "-C", cloneStage };
            //CSJ,variable=configuration,構成
            using var configuration = cloneStage is null ? ProjectListReplacement.Open(Path.Combine(directory, ".git", "config")) : null;
            if (cloneStage is null)
            {
                // Inspect the locked config with the same held machine Git that will
                // start HTTPS and GCM. A different executable on PATH cannot attest it.
                //CSJ,variable=names,名前一覧
                var names = await RunAsync([.. destination, "config", "--local", "--no-includes", "--name-only", "-z", "--list"],
                    token, trustedProcess: true, pullSettings: [], outputLimit: 1024 * 1024,
                    githubTransport: profile with { Cached = false }).ConfigureAwait(false);
                if (!VerifiedRemoteNames(names, endpoint, rejectUnsupported: true))
                    return UnsupportedGitHubRepository();
                if (verifyApprovedOrigin)
                {
                    var origin = await RunAsync([.. destination, "config", "--local", "--no-includes", "-z", "--get-all", "remote.origin.url"],
                        token, trustedProcess: true, pullSettings: [], outputLimit: 1024 * 1024,
                        githubTransport: profile with { Cached = false }).ConfigureAwait(false);
                    if (!origin.Success || origin.StandardError.Length != 0 || origin.StandardOutput != endpoint + '\0')
                        return UnsafeRemote("The origin differs from the approved endpoint. Review the access target again.");
                    BeforeRemoteExecution?.Invoke(token);
                }
            }
            //CSJ,variable=transportSettings,通信設定
            var transportSettings = BuildGitHubTransportSettings(settings, managerFile is null ? null : manager);
            return await RunAsync([.. destination, .. arguments], token, allowedProtocol: "https",
                trustedProcess: true, pullSettings: transportSettings, githubTransport: profile).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return UnsupportedGitHubInstallation();
        }
    }

    //CSJ,method=BuildGitHubTransportSettings,GitHub通信設定を構築する
    internal static string[] BuildGitHubTransportSettings(string[] checkoutSettings, string? manager)
    {
        //CSJ,variable=settings,設定
        string[] settings = [.. checkoutSettings, "-c", "http.followRedirects=false", "-c", "http.sslVerify=true",
            "-c", "http.sslBackend=schannel", "-c", "http.proxy=", "-c", "http.extraHeader=",
            "-c", "credential.helper=", "-c", "credential.interactive=false"];
        if (manager is null) return settings;
        // The helper argument is constructed exclusively from the held installation path.
        return [.. settings, "-c", "credential.https://github.com.helper=!f() { if [ \"$1\" = get ]; then '"
            + manager.Replace('\\', '/') + "' get; fi; }; f"];
    }

    private static GitResult UnsupportedGitHubInstallation() =>
        UnsafeRemote("Verified Git for Windows and, for cached authentication, Git Credential Manager are required in the machine installation.");

    internal static void RemoveGitHubChildOverrides(IDictionary<string, string?> environment)
    {
        string[] prefixes = ["GCM_", "DOTNET_", "COMPlus_", "CORECLR_", "COR_", "APPDOMAIN_MANAGER_", "BASH_FUNC_", "SSL_CERT_"];
        //CSJ,variable=names,名前一覧
        string[] names = ["GH_TOKEN", "GITHUB_TOKEN", "SSLKEYLOGFILE", "CURL_CA_BUNDLE", "CURL_HOME",
            "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
            "DEVPATH", "NETRC", "BASH_ENV", "ENV", "SHELLOPTS", "BASHOPTS", "LD_PRELOAD", "LD_LIBRARY_PATH"];
        //CSJ,variable=key,キー
        foreach (var key in environment.Keys.Where(key => names.Contains(key, StringComparer.OrdinalIgnoreCase) ||
            prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToArray())
            environment.Remove(key);
    }

    //CSJ,record=GitHubTransportProfile,GitHub通信プロファイル
    private sealed record GitHubTransportProfile(string Installation, string Executable, string? CloneCeiling, bool Cached)
    {
        internal void Configure(IDictionary<string, string?> environment)
        {
            // Git invokes the helper through its shell, and GCM is a managed app.
            // Startup/profiling and tracing overrides must not escape the Git policy.
            RemoveGitHubChildOverrides(environment);
            environment["PATH"] = string.Join(Path.PathSeparator,
                Path.Combine(Installation, "mingw64", "bin"), Path.Combine(Installation, "usr", "bin"), Environment.SystemDirectory);
            if (CloneCeiling is not null) environment["GIT_CEILING_DIRECTORIES"] = CloneCeiling;
            if (!Cached) return;
            environment["GCM_INTERACTIVE"] = "0";
            environment["GCM_PROVIDER"] = "github";
            environment["GCM_CREDENTIAL_STORE"] = "wincredman";
        }
    }
}
