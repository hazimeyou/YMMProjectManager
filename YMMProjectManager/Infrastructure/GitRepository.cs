using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.ComponentModel;
using System.Text;

namespace YMMProjectManager.Infrastructure.Git;

/// <summary>Runs local Git operations without invoking a shell or changing the process working directory.</summary>
public sealed class GitRepository
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private const string BranchRefPrefix = "refs/heads/";
    private static readonly string[] PullOperationMarkers =
    [
        "HEAD.lock", "ORIG_HEAD.lock", "index.lock", "packed-refs.lock",
        "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "BISECT_START",
        "rebase-apply", "rebase-merge", "sequencer",
    ];
    private readonly string directory;
    internal Action<CancellationToken>? BeforeRemoteExecution { get; set; }
    internal Action? BeforePullInspectionRecheck { get; set; }
    internal TimeSpan RemoteTimeout { get; init; } = Timeout;

    public GitRepository(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(this.directory))
            throw new DirectoryNotFoundException(this.directory);
    }

    public Task<GitResult> InitializeAsync(CancellationToken token = default) => RunAsync(["init"], token);
    public Task<GitResult> DetectAsync(CancellationToken token = default) => RunAsync(["rev-parse", "--show-toplevel"], token);
    public Task<GitResult> StatusAsync(CancellationToken token = default) => RunAsync(["status", "--porcelain=v1", "-z"], token);
    public Task<GitResult> HistoryAsync(CancellationToken token = default) => RunAsync(["log", "-n", "100", "--encoding=UTF-8", "--format=%H%x00%an%x00%aI%x00%s"], token);
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
    public Task<GitResult> CreateBranchAsync(string name, CancellationToken token = default) => RunBranchAsync(["branch"], name, token);
    public Task<GitResult> SwitchBranchAsync(string name, CancellationToken token = default) => RunBranchAsync(["switch", "--no-overwrite-ignore"], name, token);

    public Task<GitResult> InspectRemoteAsync(GitRemoteOperation operation, CancellationToken token = default) =>
        WithinRemoteDeadlineAsync(operation.ToString(), async deadline =>
        {
            if (operation is not (GitRemoteOperation.Fetch or GitRemoteOperation.Push))
                throw new ArgumentOutOfRangeException(nameof(operation));
            if (!HasDirectRepository()) return UnsafeRemote("Select the main repository root on a regular filesystem path.");
            using var lease = GitRepositoryLease.TryOpen(directory);
            if (lease is null)
                return UnsafeRemote("Could not verify the repository and its Git directory identity.");
            var (url, error) = await ResolveOriginAsync(operation == GitRemoteOperation.Push, deadline, literal: true).ConfigureAwait(false);
            if (error is not null) return error;
            var result = new GitResult(0, url!, "");
            if (operation == GitRemoteOperation.Fetch)
                return result with { FetchTarget = new GitFetchTarget(directory, url!, lease.Identity) };
            var (target, tipError) = await ReadPushTargetAsync(url!, trusted: true, deadline, lease.Identity).ConfigureAwait(false);
            return tipError ?? result with { PushTarget = target };
        }, token, RemoteTimeout);

    public Task<GitResult> FetchAsync(CancellationToken token = default) =>
        WithinRemoteDeadlineAsync("Fetch", deadline => FetchCoreAsync(null, deadline), token, RemoteTimeout);

    public Task<GitResult> FetchTrustedAsync(string expectedRemote, CancellationToken token = default)
    {
        ValidateRemote(expectedRemote);
        return WithinRemoteDeadlineAsync("Fetch", deadline => FetchCoreAsync(expectedRemote, deadline), token, RemoteTimeout);
    }

    public Task<GitResult> FetchTrustedAsync(GitFetchTarget target, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return WithinRemoteDeadlineAsync("Fetch", deadline => FetchCoreAsync(target.Endpoint, deadline, target), token, RemoteTimeout);
    }

    private async Task<GitResult> FetchCoreAsync(string? expectedRemote, CancellationToken token, GitFetchTarget? approvedTarget = null)
    {
        var trusted = expectedRemote is not null;
        if (trusted && !HasDirectRepository()) return UnsafeRemote("Select the main repository root on a regular filesystem path.");
        if (approvedTarget is not null && !string.Equals(directory, approvedTarget.RepositoryRoot, StringComparison.OrdinalIgnoreCase))
            return UnsafeRemote("This fetch approval belongs to a different repository.");
        using var lease = approvedTarget is null ? null : GitRepositoryLease.TryOpen(directory);
        if (approvedTarget is not null && lease?.Identity != approvedTarget.Identity)
            return UnsafeRemote("The approved repository or its Git directory changed. Review the fetch target again.");
        var (url, error) = await ResolveOriginAsync(push: false, token, literal: trusted).ConfigureAwait(false);
        if (error is not null) return error;
        if (!CanUseRemote(url!, expectedRemote)) return UnsafeRemote("The origin differs from the approved endpoint or requires explicit trust.");
        BeforeRemoteExecution?.Invoke(token);
        if (trusted && !HasDirectRepository()) return UnsafeRemote("Repository path changed before fetch.");
        return await RunRemoteAsync(url!, ["fetch", "--no-recurse-submodules", "--", url!, "+refs/heads/*:refs/remotes/origin/*"], token, trusted).ConfigureAwait(false);
    }

    /// <summary>Assesses the locally fetched origin branch without fetching or modifying the repository.</summary>
    public async Task<GitPullAssessment> InspectPullAsync(CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RemoteTimeout);
        try { return await InspectPullCoreAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { return new GitPullAssessment(GitPullState.Unavailable, Message: "Pull inspection timed out."); }
        catch (TimeoutException)
        { return new GitPullAssessment(GitPullState.Unavailable, Message: "Pull inspection timed out."); }
    }

    private async Task<GitPullAssessment> InspectPullCoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!HasDirectRepository())
            return new GitPullAssessment(GitPullState.Unavailable, Message: "Select the main repository root on a regular filesystem path.");
        using var lease = GitRepositoryLease.TryOpen(directory);
        if (lease is null)
            return new GitPullAssessment(GitPullState.Unavailable, Message: "Could not verify the repository and its Git directory identity.");
        var (branch, error) = await ReadBranchTipAsync(trusted: true, token).ConfigureAwait(false);
        if (error is not null) return new GitPullAssessment(GitPullState.Unavailable, Message: error.StandardError);
        var tracking = await ReadPullTrackingAsync(branch!.BranchRef, token).ConfigureAwait(false);
        if (tracking.Error is not null)
            return new GitPullAssessment(GitPullState.Unavailable, branch.BranchRef, branch.ObjectId, Message: tracking.Error);
        var worktree = await ReadPullWorktreeAsync(branch.BranchRef, tracking.Ref, token).ConfigureAwait(false);
        if (worktree.Blocked is { } blocked)
            return new GitPullAssessment(blocked, branch.BranchRef, branch.ObjectId, tracking.Ref, tracking.ObjectId, worktree.Message);

        var state = worktree.Status!.Length != 0 ? GitPullState.Dirty : tracking.ObjectId is null
            ? GitPullState.TrackingUnavailable : await ClassifyPullAsync(branch.ObjectId, tracking.ObjectId, token).ConfigureAwait(false);

        var checkIgnoredPaths = state == GitPullState.FastForwardCandidate;
        if (checkIgnoredPaths)
            state = await InspectIgnoredPathsAsync(branch.ObjectId, tracking.ObjectId!, token).ConfigureAwait(false);

        // The result describes these observations only; no ref or index transaction is held.
        BeforePullInspectionRecheck?.Invoke();
        var (latestBranch, _) = await ReadBranchTipAsync(trusted: true, token).ConfigureAwait(false);
        var latestTracking = await ReadPullTrackingAsync(branch.BranchRef, token).ConfigureAwait(false);
        var latestWorktree = await ReadPullWorktreeAsync(branch.BranchRef, tracking.Ref, token).ConfigureAwait(false);
        if (!HasDirectRepository() || latestBranch != branch || latestTracking != tracking || latestWorktree != worktree)
            return new GitPullAssessment(GitPullState.Changed, Message: "The repository changed during inspection. Refresh the assessment.");

        if (checkIgnoredPaths && await InspectIgnoredPathsAsync(branch.ObjectId, tracking.ObjectId!, token).ConfigureAwait(false) != state)
            return new GitPullAssessment(GitPullState.Changed, Message: "Ignored paths changed during inspection. Refresh the assessment.");

        var message = state == GitPullState.IgnoredPathCollision
            ? "An ignored path overlaps a path in the incoming changes."
            : tracking.Message;
        return new GitPullAssessment(state, branch.BranchRef, branch.ObjectId, tracking.Ref, tracking.ObjectId, message);
    }

    private async Task<GitPullState> InspectIgnoredPathsAsync(string local, string upstream, CancellationToken token)
    {
        // Normal porcelain omits ignored local files. Compare incoming changes with the ignored
        // paths themselves rather than treating all ignored files as a checkout obstruction.
        var ignored = await RunDirectAsync(["ls-files", "-z", "--others", "--ignored", "--exclude-standard"], token).ConfigureAwait(false);
        if (!ignored.Success || ignored.StandardError.Length != 0) return GitPullState.Unavailable;
        if (ignored.StandardOutput.Length == 0) return GitPullState.FastForwardCandidate;

        var changed = await RunDirectAsync(["diff-tree", "-z", "--name-only", "--no-commit-id", "--no-renames",
            "--no-ext-diff", "--no-textconv", "--diff-filter=AMT", "-r", local, upstream, "--"], token).ConfigureAwait(false);
        if (!changed.Success || changed.StandardError.Length != 0) return GitPullState.Unavailable;

        var incoming = changed.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var incomingParents = incoming.SelectMany(ParentPaths).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in ignored.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            token.ThrowIfCancellationRequested();
            var file = path.TrimEnd('/');
            if (incoming.Contains(file) || incomingParents.Contains(file) || ParentPaths(file).Any(incoming.Contains))
                return GitPullState.IgnoredPathCollision;
        }
        return GitPullState.FastForwardCandidate;

        static IEnumerable<string> ParentPaths(string path)
        {
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                yield return path[..slash];
        }
    }

    private sealed record PullTrackingObservation(string? Ref = null, string? ObjectId = null,
        string? Remote = null, string? RemoteRef = null, string? Error = null, string? Message = null);

    private async Task<PullTrackingObservation> ReadPullTrackingAsync(string branchRef, CancellationToken token)
    {
        var branchName = branchRef[BranchRefPrefix.Length..];
        var remoteSetting = await ReadSettingAsync("remote").ConfigureAwait(false);
        var mergeSetting = await ReadSettingAsync("merge").ConfigureAwait(false);
        if (remoteSetting is null || mergeSetting is null)
            return new(Error: "Could not verify the branch tracking configuration.");

        string trackingRef;
        string? remote = null;
        string? remoteRef = null;
        if (remoteSetting.Length == 0 && mergeSetting.Length == 0)
        {
            // Retain the convention used by this application's explicit origin fetch.
            trackingRef = "refs/remotes/origin/" + branchName;
        }
        else
        {
            if (remoteSetting.Length != 1 || mergeSetting.Length != 1 || remoteSetting[0] is "" or "." ||
                !mergeSetting[0].StartsWith(BranchRefPrefix, StringComparison.Ordinal))
                return new(Error: "The branch does not have a single configured remote tracking branch.");

            remote = remoteSetting[0];
            remoteRef = mergeSetting[0];
            var mapping = await RunDirectAsync(
                ["for-each-ref", "--format=%(refname)%00%(upstream)%00%(upstream:remotename)%00%(upstream:remoteref)", "--", branchRef],
                token).ConfigureAwait(false);
            var fields = mapping.StandardOutput.TrimEnd('\r', '\n').Split('\0');
            if (!mapping.Success || fields.Length != 4 || fields[0] != branchRef || fields[2] != remote ||
                fields[3] != remoteRef || !fields[1].StartsWith("refs/remotes/", StringComparison.Ordinal) || HasHiddenText(fields[1]))
                return new(Error: "Could not resolve the configured remote tracking branch.");
            trackingRef = fields[1];
        }

        var result = await RunDirectAsync(["rev-parse", "--verify", "--quiet", trackingRef + "^{commit}"], token).ConfigureAwait(false);
        var objectId = result.StandardOutput.Trim();
        if (!result.Success && result.ExitCode == 1 && objectId.Length == 0 && result.StandardError.Length == 0)
            return new(trackingRef, Remote: remote, RemoteRef: remoteRef, Message: "There is no locally saved commit for the tracking branch.");
        if (!result.Success || !IsObjectId(objectId))
            return new(Error: "Could not verify the locally saved tracking commit.");
        return new(trackingRef, objectId, remote, remoteRef);

        async Task<string[]?> ReadSettingAsync(string key)
        {
            var setting = await RunDirectAsync(["config", "--null", "--get-all", $"branch.{branchName}.{key}"], token).ConfigureAwait(false);
            if (setting.Success && setting.StandardOutput.EndsWith('\0')) return setting.StandardOutput[..^1].Split('\0');
            if (setting.ExitCode == 1 && setting.StandardOutput.Length == 0 && setting.StandardError.Length == 0) return [];
            return null;
        }
    }

    private sealed record PullWorktreeObservation(
        GitPullState? Blocked = null, string? Message = null, string? Index = null, string? Status = null);

    private async Task<PullWorktreeObservation> ReadPullWorktreeAsync(string branch, string? tracking, CancellationToken token)
    {
        try
        {
            var gitDirectory = Path.Combine(directory, ".git");
            if (HasGitEntry(Path.Combine(gitDirectory, "info", "grafts")))
                return new(GitPullState.HistoryNotVerifiable, "Legacy grafts can change the local view of commit ancestry.");

            var markers = PullOperationMarkers.Append(branch + ".lock");
            if (tracking is not null) markers = markers.Append(tracking + ".lock");
            foreach (var marker in markers)
            {
                token.ThrowIfCancellationRequested();
                if (HasGitEntry(Path.Combine(gitDirectory, marker)))
                    return new(GitPullState.OperationInProgress, "A Git lock or unfinished operation is present.");
            }
        }
        catch (IOException) { return new(GitPullState.Unavailable, "Could not verify Git operation markers."); }
        catch (UnauthorizedAccessException) { return new(GitPullState.Unavailable, "Could not verify Git operation markers."); }

        var index = await RunDirectAsync(["ls-files", "--stage", "-v", "-z"], token).ConfigureAwait(false);
        if (!index.Success)
            return new(GitPullState.Unavailable, "Could not inspect the Git index.");
        // Lowercase marks assume-unchanged entries. S marks skip-worktree, including sparse checkout.
        var indexEntries = index.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (indexEntries.Any(entry => entry[0] is 'S' or (>= 'a' and <= 'z')))
            return new(GitPullState.IndexNotVerifiable, "The Git index can conceal changes in the worktree.");
        if (indexEntries.Any(entry => entry.Length >= 2 && entry.AsSpan(2).StartsWith("160000 ", StringComparison.Ordinal)))
            return new(GitPullState.IndexNotVerifiable, "Submodule worktrees need a separate assessment.");

        // Configured clean/process filters can run while Git compares worktree contents.
        // Leave such worktrees unverified rather than requesting their status.
        var filters = await RunDirectAsync(
            ["config", "--name-only", "--get-regexp", @"^filter\."], token).ConfigureAwait(false);
        if (filters.Success)
            return new(GitPullState.IndexNotVerifiable, "Configured Git filters prevent a read-only worktree assessment.");
        if (filters.ExitCode != 1 || filters.StandardOutput.Length != 0 || filters.StandardError.Length != 0)
            return new(GitPullState.Unavailable, "Could not verify Git filter configuration.");

        var status = await RunDirectAsync(
            ["--no-optional-locks", "status", "--porcelain=v1", "-z", "--untracked-files=normal", "--ignore-submodules=none"], token).ConfigureAwait(false);
        return status.Success
            ? new(Index: index.StandardOutput, Status: status.StandardOutput)
            : new(GitPullState.Unavailable, "Could not inspect the worktree and index.");
    }

    private static bool HasGitEntry(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private async Task<GitPullState> ClassifyPullAsync(string local, string upstream, CancellationToken token)
    {
        if (local == upstream) return GitPullState.UpToDate;
        var forward = await RunDirectAsync(["-c", "advice.graftFileDeprecated=false", "merge-base", "--is-ancestor", local, upstream], token, ignoreLegacyGrafts: true).ConfigureAwait(false);
        if (forward.StandardError.Length != 0) return GitPullState.Unavailable;
        if (forward.Success) return GitPullState.FastForwardCandidate;
        if (forward.ExitCode != 1) return GitPullState.Unavailable;
        var backward = await RunDirectAsync(["-c", "advice.graftFileDeprecated=false", "merge-base", "--is-ancestor", upstream, local], token, ignoreLegacyGrafts: true).ConfigureAwait(false);
        if (backward.StandardError.Length != 0) return GitPullState.Unavailable;
        if (backward.Success) return GitPullState.LocalAhead;
        if (backward.ExitCode != 1) return GitPullState.Unavailable;
        var shallow = await RunDirectAsync(["rev-parse", "--is-shallow-repository"], token).ConfigureAwait(false);
        return shallow.Success && shallow.StandardOutput.Trim() == "false" ? GitPullState.Diverged : GitPullState.Unavailable;
    }

    public Task<GitResult> PullAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(UnsafeRemote("Pull is disabled pending recoverable updates across the branch, index and worktree."));
    }

    public Task<GitResult> PushAsync(CancellationToken token = default) =>
        WithinRemoteDeadlineAsync("Push", deadline => PushCoreAsync(null, deadline), token, RemoteTimeout);

    public Task<GitResult> PushTrustedAsync(string expectedRemote, CancellationToken token = default)
    {
        ValidateRemote(expectedRemote);
        return WithinRemoteDeadlineAsync("Push", deadline => PushCoreAsync(expectedRemote, deadline), token, RemoteTimeout);
    }

    public Task<GitResult> PushTrustedAsync(GitPushTarget target, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return WithinRemoteDeadlineAsync("Push", deadline => PushCoreAsync(target.Endpoint, deadline, target), token, RemoteTimeout);
    }

    private async Task<GitResult> PushCoreAsync(string? expectedRemote, CancellationToken token, GitPushTarget? approvedTarget = null)
    {
        var trusted = expectedRemote is not null;
        if (trusted && !HasDirectRepository()) return UnsafeRemote("Select the main repository root on a regular filesystem path.");
        if (approvedTarget is not null && !string.Equals(directory, approvedTarget.RepositoryRoot, StringComparison.OrdinalIgnoreCase))
            return UnsafeRemote("This push approval belongs to a different repository.");
        using var lease = approvedTarget is null ? null : GitRepositoryLease.TryOpen(directory);
        if (approvedTarget is not null && (lease is null || lease.Identity != approvedTarget.Identity))
            return UnsafeRemote("The approved repository or its Git directory changed. Review the push target again.");
        var (url, error) = await ResolveOriginAsync(push: true, token, literal: trusted).ConfigureAwait(false);
        if (error is not null) return error;
        if (!CanUseRemote(url!, expectedRemote)) return UnsafeRemote("The origin differs from the approved endpoint or requires explicit trust.");
        var (current, tipError) = await ReadPushTargetAsync(url!, trusted, token, lease?.Identity).ConfigureAwait(false);
        if (tipError is not null) return tipError;
        if (approvedTarget is not null && approvedTarget != current)
            return UnsafeRemote("The branch or commit changed since approval. Review the push target again.");
        BeforeRemoteExecution?.Invoke(token);
        if (trusted && !HasDirectRepository()) return UnsafeRemote("Repository path changed before push.");
        if (approvedTarget is not null)
        {
            var (lastTarget, _) = await ReadPushTargetAsync(url!, trusted: true, token, lease!.Identity).ConfigureAwait(false);
            if (approvedTarget != lastTarget)
                return UnsafeRemote("The branch or commit changed before push. Review the push target again.");
        }

        // Pass the same approved object and full destination branch even if HEAD changes
        // after the final observation. Never ask Git to resolve a mutable source ref.
        var target = approvedTarget ?? current!;
        return await RunRemoteAsync(target.Endpoint,
            ["-c", "push.followTags=false", "push", "--porcelain", "--no-follow-tags", "--recurse-submodules=no",
             "--", target.Endpoint, target.ObjectId + ":" + target.BranchRef], token, trusted, classifyPushStatus: true).ConfigureAwait(false);
    }

    private async Task<(GitPushTarget? Target, GitResult? Error)> ReadPushTargetAsync(string url, bool trusted, CancellationToken token,
        GitRepositoryIdentity? identity)
    {
        var (branch, error) = await ReadBranchTipAsync(trusted, token).ConfigureAwait(false);
        return error is not null ? (null, error) : (new GitPushTarget(directory, url, branch!.BranchRef, branch.ObjectId, identity), null);
    }

    private sealed record BranchTip(string BranchRef, string ObjectId);

    private async Task<(BranchTip? Branch, GitResult? Error)> ReadBranchTipAsync(bool trusted, CancellationToken token)
    {
        var branch = await ReadGitAsync(["symbolic-ref", "--quiet", "HEAD"]).ConfigureAwait(false);
        if (!branch.Success) return (null, new GitResult(branch.ExitCode, "", "A branch must be checked out."));
        var name = branch.StandardOutput.TrimEnd('\r', '\n');
        if (!name.StartsWith(BranchRefPrefix, StringComparison.Ordinal) || name.Length == BranchRefPrefix.Length || HasHiddenText(name))
            return (null, new GitResult(1, "", "Invalid branch name."));
        var commit = await ReadGitAsync(["rev-parse", "--verify", name + "^{commit}"]).ConfigureAwait(false);
        var objectId = commit.StandardOutput.Trim();
        if (!commit.Success || !IsObjectId(objectId))
            return (null, new GitResult(1, "", "Could not determine the branch commit."));
        return (new BranchTip(name, objectId), null);

        Task<GitResult> ReadGitAsync(string[] args) => trusted ? RunDirectAsync(args, token) : RunAsync(args, token);
    }

    private Task<GitResult> RunDirectAsync(string[] args, CancellationToken token, bool ignoreLegacyGrafts = false) =>
        RunAsync([$"--git-dir={Path.Combine(directory, ".git")}", $"--work-tree={directory}", .. args], token,
            trustedProcess: true, ignoreLegacyGrafts: ignoreLegacyGrafts);

    private static bool IsObjectId(string value) => (value.Length == 40 || value.Length == 64) && value.All(Uri.IsHexDigit);

    private async Task<GitResult> RunRemoteAsync(string url, string[] arguments, CancellationToken token,
        bool trusted, bool classifyPushStatus = false, string? cloneStage = null)
    {
        var protocol = Path.IsPathFullyQualified(url) ? "file" : "https";
        if (trusted)
        {
            // A literal origin is the approval target. Refuse configured rewrites, including
            // push-only rewrites that ls-remote cannot establish for the push operation.
            var rewrites = await RunAsync(["config", "--name-only", "--get-regexp", @"^url\..*\.(insteadof|pushinsteadof)$"], token, trustedProcess: true).ConfigureAwait(false);
            if (rewrites.ExitCode != 1 || !string.IsNullOrWhiteSpace(rewrites.StandardOutput))
                return UnsafeRemote("Git URL rewrites are configured. The approved endpoint cannot be verified.");
        }

        var effective = await RunAsync(["ls-remote", "--get-url", "--", url], token, trustedProcess: true).ConfigureAwait(false);
        if (!effective.Success || !string.Equals(effective.StandardOutput.TrimEnd('\r', '\n'), url, StringComparison.Ordinal))
            return UnsafeRemote("Git rewrites the selected remote. Confirm the repository configuration before retrying.");

        if (protocol == "https")
        {
            // Scoped settings take precedence over less-specific HTTP configuration.
            // ValidateRemote excludes a literal '=' because Git's -c splits key and value there.
            arguments = ["-c", "http.followRedirects=false", "-c", $"http.{url}.followRedirects=false",
                "-c", $"http.{url}.sslVerify=true", .. arguments];
        }

        if (trusted && cloneStage is null)
        {
            if (!HasDirectRepository()) return UnsafeRemote("Repository path changed before execution.");
            arguments = [$"--git-dir={Path.Combine(directory, ".git")}", $"--work-tree={directory}", .. arguments];
        }
        return SanitizeRemote(await RunAsync(arguments, token, protocol).ConfigureAwait(false), classifyPushStatus, cloneStage);
    }

    public static GitResult InspectCloneRemote(string remote)
    {
        try { ValidateRemote(remote); return new GitResult(0, remote, ""); }
        catch (ArgumentException) { return UnsafeRemote("Clone source is unsupported or contains credentials."); }
    }

    public static Task<GitResult> CloneAsync(string remote, string destination, CancellationToken token = default) =>
        CloneCoreAsync(remote, destination, null, token);

    public static Task<GitResult> CloneTrustedAsync(string remote, string destination, CancellationToken token = default) =>
        WithinRemoteDeadlineAsync("Clone", deadline => CloneCoreAsync(remote, destination, null, deadline, trusted: true), token, Timeout);

    internal static Task<GitResult> CloneCoreAsync(string remote, string destination, Action? beforeMove, CancellationToken token) =>
        WithinRemoteDeadlineAsync("Clone", deadline => CloneCoreAsync(remote, destination, beforeMove, deadline, trusted: false), token, Timeout);

    private static async Task<GitResult> CloneCoreAsync(string remote, string destination, Action? beforeMove, CancellationToken token, bool trusted)
    {
        token.ThrowIfCancellationRequested();
        ValidateRemote(remote);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var full = Path.GetFullPath(destination);
        if (Directory.Exists(full) || File.Exists(full)) throw new IOException("Clone destination already exists.");
        var parent = Path.GetDirectoryName(full) ?? throw new ArgumentException("Invalid clone destination.", nameof(destination));
        if (!trusted && !Path.IsPathFullyQualified(remote)) return UnsafeRemote("Cloning an HTTPS origin requires explicit trust.");
        GitCloneDirectory reserved;
        try { reserved = GitCloneDirectory.Reserve(parent); }
        catch (IOException)
        { return UnsafeRemote("Could not safely reserve a clone directory under the selected parent."); }
        using var staging = reserved;
        var repository = new GitRepository(parent);
        GitResult result;
        try
        {
            result = await repository.RunRemoteAsync(remote, ["clone", "--no-recurse-submodules", "--", remote, staging.Path],
                token, trusted, cloneStage: staging.Path).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ex.Data["CloneRecoveryPath"] = staging.CheckRecoveryPath();
            throw;
        }
        if (!result.Success) return result with { RecoveryPath = staging.CheckRecoveryPath() };
        try
        {
            beforeMove?.Invoke();
            staging.Publish(full, token);
            return result;
        }
        catch (IOException)
        {
            return new GitResult(1, "", "Clone could not be safely published. Check the destination and staging directory.",
                GitFailure.UnsafeOperation) { RecoveryPath = staging.CheckRecoveryPath() };
        }
        catch (Exception ex)
        {
            ex.Data["CloneRecoveryPath"] = staging.CheckRecoveryPath();
            throw;
        }
    }

    private async Task<(string? Url, GitResult? Error)> ResolveOriginAsync(bool push, CancellationToken token, bool literal = false)
    {
        GitResult result;
        if (literal)
        {
            result = await ReadLocalSettingAsync(push ? "remote.origin.pushurl" : "remote.origin.url").ConfigureAwait(false);
            if (push && result.ExitCode == 1)
                result = await ReadLocalSettingAsync("remote.origin.url").ConfigureAwait(false);
        }
        else
        {
            var args = push ? new[] { "remote", "get-url", "--push", "--all", "origin" } : new[] { "remote", "get-url", "--all", "origin" };
            result = await RunAsync(args, token).ConfigureAwait(false);
        }
        if (!result.Success) return (null, new GitResult(result.ExitCode, "", "Remote origin is unavailable."));
        var urls = literal ? result.StandardOutput.Split('\0')[..^1] :
            result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (urls.Length != 1) return (null, new GitResult(1, "", "Exactly one origin URL is required."));
        try { ValidateRemote(urls[0]); }
        catch (ArgumentException) { return (null, new GitResult(1, "", "Remote URL is unsupported or contains credentials.")); }
        return (urls[0], null);

        Task<GitResult> ReadLocalSettingAsync(string key) =>
            RunAsync([$"--git-dir={Path.Combine(directory, ".git")}", "config", "--local", "--no-includes", "-z", "--get-all", key], token, trustedProcess: true);
    }

    private static GitResult UnsafeRemote(string message) => new(1, "", message, GitFailure.UnsafeOperation);

    private static bool CanUseRemote(string url, string? expectedRemote) => expectedRemote is null
        ? Path.IsPathFullyQualified(url)
        : string.Equals(url, expectedRemote, StringComparison.Ordinal);

    private bool HasDirectRepository()
    {
        var gitDirectory = Path.Combine(directory, ".git");
        return !HasHiddenText(directory) && IsOrdinaryDirectory(gitDirectory) && IsOrdinaryDirectory(Path.Combine(gitDirectory, "objects")) &&
            IsRegularFile(Path.Combine(gitDirectory, "config")) && IsRegularFile(Path.Combine(gitDirectory, "HEAD")) &&
            !Path.Exists(Path.Combine(gitDirectory, "commondir"));
    }

    private static bool IsOrdinaryDirectory(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            if (!HasAttributes(directory.FullName, FileAttributes.Directory, FileAttributes.ReparsePoint)) return false;
        }
        return true;
    }

    private static bool IsRegularFile(string path) => HasAttributes(path, 0, FileAttributes.Directory | FileAttributes.ReparsePoint);

    private static bool HasAttributes(string path, FileAttributes required, FileAttributes forbidden)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & required) == required && (attributes & forbidden) == 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static async Task<GitResult> WithinRemoteDeadlineAsync(string operation, Func<CancellationToken, Task<GitResult>> run,
        CancellationToken token, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        GitResult result;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            result = await run(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            result = new GitResult(1, "", "Git remote operation timed out. Refresh remote state before retrying.", GitFailure.Timeout)
                { RecoveryPath = ex.Data["CloneRecoveryPath"] as string };
        }
        catch (TimeoutException)
        {
            result = new GitResult(1, "", "Git remote operation timed out. Refresh remote state before retrying.", GitFailure.Timeout);
        }
        return result with { RemoteDiagnostic = new GitRemoteDiagnostic(operation, clock.Elapsed) };
    }

    private static bool HasHiddenText(string value) =>
        value.EnumerateRunes().Any(character => Rune.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.Format);

    private static void ValidateRemote(string remote)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);
        if (remote.StartsWith('-') || HasHiddenText(remote))
            throw new ArgumentException("Invalid remote.", nameof(remote));
        if (Path.IsPathFullyQualified(remote) && !remote.StartsWith("\\\\", StringComparison.Ordinal)) return;
        if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || remote.Any(char.IsWhiteSpace) || remote.Contains('\\') || remote.Contains('='))
            throw new ArgumentException("Only unambiguous local paths and HTTPS URLs without credentials are supported.", nameof(remote));
    }

    // Git, credential helpers and hooks can emit arbitrary secrets; do not return their output for remote operations.
    private static GitResult SanitizeRemote(GitResult result, bool classifyPushStatus, string? cloneStage)
    {
        if (result.Success) return new GitResult(result.ExitCode, "", "");
        var category = classifyPushStatus && HasNonFastForwardRejection(result.StandardOutput)
            ? GitFailure.Divergence : ClassifyRemoteError(result.StandardError, cloneStage);
        var message = category == GitFailure.PathTooLong
            ? "Git could not represent a filesystem path. Use a shorter repository or clone destination path."
            : "Remote operation failed. Check authentication, connectivity and branch state.";
        return new GitResult(result.ExitCode, "", message, category);
    }

    private static GitFailure ClassifyRemoteError(string error, string? cloneStage)
    {
        bool Contains(string value) => error.Contains(value, StringComparison.OrdinalIgnoreCase);
        if (Contains("filename too long") || Contains("file name too long") || Contains("'$GIT_DIR' too big"))
            return GitFailure.PathTooLong;
        // Some versions can clone objects at these lengths and still fail to start checkout.
        if (cloneStage is { Length: >= 260 } && Contains("Clone succeeded, but checkout failed") &&
            Contains("this operation must be run in a work tree"))
            return GitFailure.PathTooLong;
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

    private Task<GitResult> RunBranchAsync(string[] command, string name, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.StartsWith('-') || name.Contains('\0')) throw new ArgumentException("Invalid branch name.", nameof(name));
        return RunAsync([.. command, name], token);
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

    private string ResolveRemoteGitExecutable()
    {
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(path)) continue;
            var candidate = Path.GetFullPath(Path.Combine(path, "git.exe"));
            var relative = Path.GetRelativePath(directory, candidate);
            if (!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative)) continue;
            if (IsOrdinaryDirectory(path) && IsRegularFile(candidate)) return candidate;
        }
        throw new InvalidOperationException("Git was not found in an absolute PATH directory outside the working repository.");
    }

    internal static void SetTransportEnvironment(IDictionary<string, string?> environment, string? allowedProtocol)
    {
        // Partial clones can fetch missing objects even during object lookups such as
        // rev-parse. A local read or consent preview must not initiate network access.
        environment["GIT_NO_LAZY_FETCH"] = "1";
        environment.TryGetValue("GIT_ALLOW_PROTOCOL", out var parentProtocols);
        environment["GIT_ALLOW_PROTOCOL"] = allowedProtocol is not null &&
            (parentProtocols is null || parentProtocols.Split(':').Contains(allowedProtocol, StringComparer.Ordinal))
            ? allowedProtocol : string.Empty;
    }

    private async Task<GitResult> RunAsync(IReadOnlyCollection<string> arguments, CancellationToken token, string? allowedProtocol = null, bool trustedProcess = false,
        bool ignoreLegacyGrafts = false)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(allowedProtocol is null && !trustedProcess ? "git" : ResolveRemoteGitExecutable())
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Git for Windows emits UTF-8 paths in NUL-terminated output.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var name in new[]
        {
            "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY",
            "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG", "GIT_NAMESPACE", "GIT_REPLACE_REF_BASE",
            "GIT_SHALLOW_FILE", "GIT_GRAFT_FILE", "GIT_QUARANTINE_PATH", "GIT_PREFIX", "GIT_IMPLICIT_WORK_TREE",
        })
            start.Environment.Remove(name);
        // Inherited command-scoped config can inject hooks or rewrite a remote independently
        // of the repository and normal user Git configuration that the UI asks users to trust.
        foreach (var name in start.Environment.Keys.Where(name =>
            name.Equals("GIT_CONFIG_COUNT", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("GIT_CONFIG_PARAMETERS", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("GIT_CONFIG_KEY_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("GIT_CONFIG_VALUE_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        // A graft can appear after the filesystem observation. Ancestry checks always use
        // the real commit objects, even while that observation is being revalidated.
        if (ignoreLegacyGrafts) start.Environment["GIT_GRAFT_FILE"] = "NUL";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        start.Environment["LC_ALL"] = "C";
        SetTransportEnvironment(start.Environment, allowedProtocol);
        if (allowedProtocol is not null || trustedProcess)
        {
            foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("GIT_TRACE", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("GIT_CURL_VERBOSE", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(name);
            start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("core.longpaths=true");
        }
        if (allowedProtocol is not null)
        {
            start.Environment.Remove("GIT_EXEC_PATH");
            start.Environment.Remove("GIT_SSL_NO_VERIFY");
            start.Environment.Remove("GIT_ASKPASS");
            start.Environment.Remove("SSH_ASKPASS");
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

public enum GitFailure { None, Authentication, Permission, Network, Divergence, Unknown, UnsafeOperation, Timeout, PathTooLong }
public enum GitRemoteOperation { Fetch, Push }
public enum GitPullState { Unavailable, TrackingUnavailable, Dirty, UpToDate, FastForwardCandidate, LocalAhead, Diverged, Changed, OperationInProgress, IndexNotVerifiable, IgnoredPathCollision, HistoryNotVerifiable }
public sealed record GitPullAssessment(GitPullState State, string? BranchRef = null, string? LocalObjectId = null,
    string? TrackingRef = null, string? TrackingObjectId = null, string? Message = null);
public sealed record GitRemoteDiagnostic(string Operation, TimeSpan Elapsed);

public sealed class GitFetchTarget
{
    internal GitFetchTarget(string repositoryRoot, string endpoint, GitRepositoryIdentity identity)
    {
        RepositoryRoot = repositoryRoot;
        Endpoint = endpoint;
        Identity = identity;
    }

    public string RepositoryRoot { get; }
    public string Endpoint { get; }
    internal GitRepositoryIdentity Identity { get; }
}

public sealed record GitPushTarget
{
    internal GitPushTarget(string repositoryRoot, string endpoint, string branchRef, string objectId, GitRepositoryIdentity? identity)
    {
        RepositoryRoot = repositoryRoot;
        Endpoint = endpoint;
        BranchRef = branchRef;
        ObjectId = objectId;
        Identity = identity;
    }

    public string RepositoryRoot { get; }
    public string Endpoint { get; }
    public string BranchRef { get; }
    public string ObjectId { get; }
    internal GitRepositoryIdentity? Identity { get; }
}

public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError, GitFailure Failure = GitFailure.None)
{
    public bool Success => ExitCode == 0;
    public string? RecoveryPath { get; init; }
    public GitRemoteDiagnostic? RemoteDiagnostic { get; init; }
    public GitPushTarget? PushTarget { get; init; }
    public GitFetchTarget? FetchTarget { get; init; }
}
