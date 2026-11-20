using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace YMMProjectManager.Infrastructure.Git;

/// <summary>Runs local Git operations without invoking a shell or changing the process working directory.</summary>
public sealed class GitRepository
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private const string BranchRefPrefix = "refs/heads/";
    private readonly string directory;
    internal Action? BeforeRemoteExecution { get; set; }

    public GitRepository(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.GetFullPath(directory);
        if (!Directory.Exists(this.directory))
            throw new DirectoryNotFoundException(this.directory);
    }

    public Task<GitResult> InitializeAsync(CancellationToken token = default) => RunAsync(["init"], token);
    public Task<GitResult> DetectAsync(CancellationToken token = default) => RunAsync(["rev-parse", "--show-toplevel"], token);
    public Task<GitResult> StatusAsync(CancellationToken token = default) => RunAsync(["status", "--porcelain=v1", "-z"], token);
    public Task<GitResult> HistoryAsync(CancellationToken token = default) => RunAsync(["log", "-n", "100", "--format=%H%x00%an%x00%aI%x00%s"], token);
    public Task<GitResult> BranchesAsync(CancellationToken token = default) => RunAsync(["branch", "--list", "--no-color"], token);
    public async Task<GitResult> OriginAsync(CancellationToken token = default)
    {
        var result = await RunAsync(["remote", "get-url", "--all", "origin"], token).ConfigureAwait(false);
        if (!result.Success) return new GitResult(result.ExitCode, "", "Remote origin is unavailable.");
        var urls = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (urls.Length != 1) return new GitResult(1, "", "Exactly one origin URL is required.");
        try { ValidateRemote(urls[0]); }
        catch (ArgumentException) { return new GitResult(1, "", "Remote URL is unsupported or contains credentials."); }
        return new GitResult(0, urls[0], "");
    }
    public Task<GitResult> StageAsync(IReadOnlyCollection<string> paths, CancellationToken token = default) => RunPathsAsync(["add", "--"], paths, token);
    public Task<GitResult> UnstageAsync(IReadOnlyCollection<string> paths, CancellationToken token = default) => RunPathsAsync(["restore", "--staged", "--"], paths, token);
    public Task<GitResult> CommitAsync(string message, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return RunAsync(["commit", "-m", message], token);
    }
    public Task<GitResult> CreateBranchAsync(string name, CancellationToken token = default) => RunBranchAsync("branch", name, token);
    public Task<GitResult> SwitchBranchAsync(string name, CancellationToken token = default) => RunBranchAsync("switch", name, token);

    public async Task<GitResult> FetchAsync(CancellationToken token = default)
    {
        var (url, error) = await ResolveOriginAsync(push: false, token).ConfigureAwait(false);
        if (error is not null) return error;
        BeforeRemoteExecution?.Invoke();
        return await RunRemoteAsync(url!, ["fetch", "--no-recurse-submodules", "--", url!, "+refs/heads/*:refs/remotes/origin/*"], token).ConfigureAwait(false);
    }

    public async Task<GitResult> PullAsync(CancellationToken token = default)
    {
        var (_, error) = await ResolveOriginAsync(push: false, token).ConfigureAwait(false);
        if (error is not null) return error;
        var branch = await RunAsync(["symbolic-ref", "--quiet", "--short", "HEAD"], token).ConfigureAwait(false);
        if (!branch.Success) return new GitResult(branch.ExitCode, "", "A branch must be checked out before pulling.");
        var name = branch.StandardOutput.Trim();
        if (name.Length == 0 || name.StartsWith('-') || name.Contains('\n') || name.Contains('\r'))
            return new GitResult(1, "", "Invalid branch name.");
        BeforeRemoteExecution?.Invoke();
        token.ThrowIfCancellationRequested();
        // Git pull updates the branch checked out when it executes, not necessarily the
        // one observed above. Another process can switch HEAD even after a recheck.
        return new GitResult(1, "", "Pull is disabled because the checked-out branch can change concurrently.", GitFailure.UnsafeOperation);
    }

    public async Task<GitResult> PushAsync(CancellationToken token = default)
    {
        var (url, error) = await ResolveOriginAsync(push: true, token).ConfigureAwait(false);
        if (error is not null) return error;
        var branch = await RunAsync(["symbolic-ref", "--quiet", "HEAD"], token).ConfigureAwait(false);
        if (!branch.Success) return new GitResult(branch.ExitCode, "", "A branch must be checked out before pushing.");
        var name = branch.StandardOutput.Trim();
        if (!name.StartsWith(BranchRefPrefix, StringComparison.Ordinal) || name.Length == BranchRefPrefix.Length || name.Contains('\n') || name.Contains('\r'))
            return new GitResult(1, "", "Invalid branch name.");
        var commit = await RunAsync(["rev-parse", "--verify", name + "^{commit}"], token).ConfigureAwait(false);
        var objectId = commit.StandardOutput.Trim();
        if (!commit.Success || (objectId.Length != 40 && objectId.Length != 64) || !objectId.All(Uri.IsHexDigit))
            return new GitResult(1, "", "Could not determine the branch commit.");
        BeforeRemoteExecution?.Invoke();
        return await RunRemoteAsync(url!, ["-c", "push.followTags=false", "push", "--porcelain", "--no-follow-tags", "--recurse-submodules=no", "--", url!, objectId + ":" + name], token, classifyPushStatus: true).ConfigureAwait(false);
    }

    private async Task<GitResult> RunRemoteAsync(string url, string[] arguments, CancellationToken token, bool classifyPushStatus = false)
    {
        // Git may apply insteadOf again when executing a command with an already resolved URL.
        // This preflight catches a stable rewrite, while protocol limits also apply at execution.
        var protocol = Path.IsPathFullyQualified(url) ? "file" : "https";
        var effective = await RunAsync(["ls-remote", "--get-url", "--", url], token, protocol).ConfigureAwait(false);
        if (!effective.Success || !string.Equals(effective.StandardOutput.TrimEnd('\r', '\n'), url, StringComparison.Ordinal))
            return new GitResult(1, "", "Git rewrites the selected remote. Confirm the repository configuration before retrying.", GitFailure.UnsafeOperation);
        return SanitizeRemote(await RunAsync(arguments, token, protocol).ConfigureAwait(false), classifyPushStatus);
    }

    public static Task<GitResult> CloneAsync(string remote, string destination, CancellationToken token = default) =>
        CloneCoreAsync(remote, destination, null, token);

    internal static async Task<GitResult> CloneCoreAsync(string remote, string destination, Action? beforeMove, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateRemote(remote);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var full = Path.GetFullPath(destination);
        if (Directory.Exists(full) || File.Exists(full)) throw new IOException("Clone destination already exists.");
        var parent = Path.GetDirectoryName(full) ?? throw new ArgumentException("Invalid clone destination.", nameof(destination));
        var repository = new GitRepository(parent);
        // Reserve an owned sibling with an exclusive OS create before Git can write to it.
        var temporary = Path.Combine(parent, ".ymmpm-clone-" + Guid.NewGuid().ToString("N"));
        if (!CreateDirectory(ToNativePath(temporary), IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        GitResult result;
        try { result = await repository.RunRemoteAsync(remote, ["clone", "--no-recurse-submodules", "--", remote, temporary], token).ConfigureAwait(false); }
        catch (Exception ex)
        {
            ex.Data["CloneRecoveryPath"] = temporary;
            throw;
        }
        if (!result.Success) return result with { RecoveryPath = temporary };
        try
        {
            beforeMove?.Invoke();
            token.ThrowIfCancellationRequested();
            Directory.Move(temporary, full);
            return result;
        }
        catch (IOException)
        {
            // The completed clone remains in the owned temporary directory for the caller to inspect.
            return new GitResult(1, "", "Clone destination became unavailable.", GitFailure.Unknown) { RecoveryPath = temporary };
        }
        catch (Exception ex)
        {
            ex.Data["CloneRecoveryPath"] = temporary;
            throw;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);

    private static string ToNativePath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
        : @"\\?\" + path;

    private async Task<(string? Url, GitResult? Error)> ResolveOriginAsync(bool push, CancellationToken token)
    {
        var args = push ? new[] { "remote", "get-url", "--push", "--all", "origin" } : new[] { "remote", "get-url", "--all", "origin" };
        var result = await RunAsync(args, token).ConfigureAwait(false);
        if (!result.Success) return (null, new GitResult(result.ExitCode, "", "Remote origin is unavailable."));
        var urls = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (urls.Length != 1) return (null, new GitResult(1, "", "Exactly one origin URL is required."));
        try { ValidateRemote(urls[0]); }
        catch (ArgumentException) { return (null, new GitResult(1, "", "Remote URL is unsupported or contains credentials.")); }
        return (urls[0], null);
    }

    private static void ValidateRemote(string remote)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);
        if (remote.StartsWith('-') || remote.Contains('\r') || remote.Contains('\n') || remote.Contains('\0'))
            throw new ArgumentException("Invalid remote.", nameof(remote));
        if (Path.IsPathFullyQualified(remote) && !remote.StartsWith("\\\\", StringComparison.Ordinal)) return;
        if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Only local paths and HTTPS URLs without credentials are supported.", nameof(remote));
    }

    // Git, credential helpers and hooks can emit arbitrary secrets; do not return their output for remote operations.
    private static GitResult SanitizeRemote(GitResult result, bool classifyPushStatus)
    {
        if (result.Success) return new GitResult(result.ExitCode, "", "");
        var category = classifyPushStatus && HasNonFastForwardRejection(result.StandardOutput)
            ? GitFailure.Divergence : ClassifyRemoteError(result.StandardError);
        return new GitResult(result.ExitCode, "", "Remote operation failed. Check authentication, connectivity and branch state.", category);
    }

    private static GitFailure ClassifyRemoteError(string error)
    {
        bool Contains(string value) => error.Contains(value, StringComparison.OrdinalIgnoreCase);
        if (Contains("authentication failed") || Contains("could not read Username")) return GitFailure.Authentication;
        if (Contains("permission denied") || Contains("requested URL returned error: 403")) return GitFailure.Permission;
        if (Contains("could not resolve host") || Contains("couldn't connect")) return GitFailure.Network;
        return GitFailure.Unknown;
    }

    private static bool HasNonFastForwardRejection(string porcelain)
    {
        foreach (var line in porcelain.Split('\n'))
        {
            if (!line.StartsWith("!\t", StringComparison.Ordinal)) continue;
            var separator = line.LastIndexOf('\t');
            var status = line[(separator + 1)..].TrimEnd('\r');
            if (status is "[rejected] (non-fast-forward)" or "[rejected] (fetch first)") return true;
        }
        return false;
    }

    private Task<GitResult> RunBranchAsync(string command, string name, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.StartsWith('-') || name.Contains('\0')) throw new ArgumentException("Invalid branch name.", nameof(name));
        return RunAsync([command, name], token);
    }

    private Task<GitResult> RunPathsAsync(string[] prefix, IReadOnlyCollection<string> paths, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0) throw new ArgumentException("At least one path is required.", nameof(paths));
        var args = new List<string>(prefix);
        foreach (var path in paths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (Path.IsPathRooted(path) || path.Contains('\0')) throw new ArgumentException("Paths must be relative to the repository.", nameof(paths));
            var fullPath = Path.GetFullPath(path, directory);
            if (!fullPath.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Path is outside the repository.", nameof(paths));
            args.Add(":(literal)" + path.Replace('\\', '/'));
        }
        return RunAsync(args, token);
    }

    private async Task<GitResult> RunAsync(IReadOnlyCollection<string> arguments, CancellationToken token, string? allowedProtocol = null)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES" })
            start.Environment.Remove(name);
        // Inherited command-scoped config can inject hooks or rewrite a remote independently
        // of the repository and normal user Git configuration that the UI asks users to trust.
        foreach (var name in start.Environment.Keys.Where(name =>
            name.Equals("GIT_CONFIG_COUNT", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("GIT_CONFIG_PARAMETERS", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("GIT_CONFIG_KEY_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("GIT_CONFIG_VALUE_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        start.Environment["LC_ALL"] = "C";
        if (allowedProtocol is not null)
        {
            var parentProtocols = start.Environment.TryGetValue("GIT_ALLOW_PROTOCOL", out var value) ? value : null;
            start.Environment["GIT_ALLOW_PROTOCOL"] = parentProtocols is null || parentProtocols.Split(':').Contains(allowedProtocol, StringComparer.Ordinal)
                ? allowedProtocol : string.Empty;
            start.Environment.Remove("GIT_EXEC_PATH");
        }
        // Status refresh must not silently start a repository-configured fsmonitor process.
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.fsmonitor=false");
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Git.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Timeout);
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try { await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException("Git operation timed out.");
        }
        return new GitResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}

public enum GitFailure { None, Authentication, Permission, Network, Divergence, Unknown, UnsafeOperation }

public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError, GitFailure Failure = GitFailure.None)
{
    public bool Success => ExitCode == 0;
    public string? RecoveryPath { get; init; }
}
