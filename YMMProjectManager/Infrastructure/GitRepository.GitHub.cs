using System.ComponentModel;
using System.IO;

namespace YMMProjectManager.Infrastructure.Git;

public enum GitHubAuthentication
{
    Anonymous,
    CachedWindows,
}

public sealed partial class GitRepository
{
    public static Task<GitResult> CloneTrustedAsync(string remote, string destination, GitHubAuthentication authentication,
        CancellationToken token = default) =>
        WithinRemoteDeadlineAsync("Clone", deadline => CloneCoreAsync(remote, destination, null, deadline, trusted: true,
            authentication), token, Timeout);

    public Task<GitResult> FetchTrustedAsync(GitFetchTarget target, GitHubAuthentication authentication, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return WithinRemoteDeadlineAsync("Fetch", deadline => WithAppWriteLeaseAsync(
            () => FetchCoreAsync(target.Endpoint, deadline, target, authentication), deadline), token, RemoteTimeout);
    }

    public Task<GitResult> PushTrustedAsync(GitPushTarget target, GitHubAuthentication authentication, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return WithinRemoteDeadlineAsync("Push", deadline => WithAppWriteLeaseAsync(
            () => PushCoreAsync(target.Endpoint, deadline, target, authentication), deadline), token, RemoteTimeout);
    }

    /// <summary>Checks access to one GitHub repository. A public repository can succeed anonymously.</summary>
    public Task<GitResult> ProbeGitHubAccessAsync(string endpoint, GitHubAuthentication authentication, CancellationToken token = default) =>
        WithinRemoteDeadlineAsync("GitHub access", async deadline =>
        {
            if (!IsSupportedGitHubEndpoint(endpoint) || !Enum.IsDefined(authentication))
                return UnsupportedGitHubEndpoint();
            using var session = await OpenPullSessionAsync(deadline, requireIndex: false).ConfigureAwait(false);
            if (session is null) return UnsupportedGitHubRepository();
            var result = await RunGitHubTransportAsync(endpoint, ["ls-remote", "--symref", "--", endpoint, "HEAD"],
                deadline, authentication, session.Settings).ConfigureAwait(false);
            return SanitizeRemote(result, classifyPushStatus: false, cloneStage: null);
        }, token, RemoteTimeout);

    private static bool HasGitHubHost(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        string.Equals(uri.IdnHost.TrimEnd('.'), "github.com", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSupportedGitHubEndpoint(string endpoint)
    {
        const string prefix = "https://github.com/";
        if (!endpoint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
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

    private async Task<GitResult> RunGitHubTransportAsync(string endpoint, string[] arguments, CancellationToken token,
        GitHubAuthentication authentication, string[] settings, string? cloneStage = null)
    {
        if (!IsSupportedGitHubEndpoint(endpoint) || !Enum.IsDefined(authentication))
            return UnsupportedGitHubEndpoint();
        var installation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git");
        var native = Path.Combine(installation, "mingw64", "bin");
        var executable = Path.Combine(native, "git.exe");
        var manager = Path.Combine(native, "git-credential-manager.exe");
        if (!Path.IsPathFullyQualified(installation) || HasHiddenText(installation) || installation.Contains('\''))
            return UnsupportedGitHubInstallation();
        try
        {
            // Only the normal machine-managed installation is accepted. PATH and
            // repository/global helpers are outside this credential trust boundary.
            using var toolchain = OwnedFilePublication.DirectoryFence.Open(native, enforceShare: true);
            using var gitFile = ProjectListReplacement.Open(executable);
            using var managerFile = authentication == GitHubAuthentication.CachedWindows ? ProjectListReplacement.Open(manager) : null;
            var profile = new GitHubTransportProfile(installation, executable, cloneStage is null ? null : directory,
                authentication == GitHubAuthentication.CachedWindows);
            var transportSettings = BuildGitHubTransportSettings(settings, managerFile is null ? null : manager);
            var destination = cloneStage is null
                ? new[] { $"--git-dir={Path.Combine(directory, ".git")}", $"--work-tree={directory}" }
                : new[] { "-C", cloneStage };
            return await RunAsync([.. destination, .. arguments], token, allowedProtocol: "https",
                trustedProcess: true, pullSettings: transportSettings, githubTransport: profile).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return UnsupportedGitHubInstallation();
        }
    }

    internal static string[] BuildGitHubTransportSettings(string[] checkoutSettings, string? manager)
    {
        string[] settings = [.. checkoutSettings, "-c", "http.followRedirects=false", "-c", "http.sslVerify=true",
            "-c", "http.sslBackend=schannel", "-c", "http.proxy=", "-c", "http.extraHeader=",
            "-c", "credential.helper=", "-c", "credential.interactive=false"];
        if (manager is null) return settings;
        // The helper argument is constructed exclusively from the held installation path.
        return [.. settings, "-c", "credential.https://github.com.helper=!'"
            + manager.Replace('\\', '/') + "'"];
    }

    private static GitResult UnsupportedGitHubInstallation() =>
        UnsafeRemote("Verified Git for Windows and, for cached authentication, Git Credential Manager are required in the machine installation.");

    private sealed record GitHubTransportProfile(string Installation, string Executable, string? CloneCeiling, bool Cached)
    {
        internal void Configure(IDictionary<string, string?> environment)
        {
            foreach (var name in environment.Keys.Where(name =>
                name.StartsWith("GCM_", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("GH_TOKEN", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("SSLKEYLOGFILE", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("CURL_CA_BUNDLE", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("SSL_CERT_", StringComparison.OrdinalIgnoreCase)).ToArray())
                environment.Remove(name);
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
