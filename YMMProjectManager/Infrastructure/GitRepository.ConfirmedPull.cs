using System.Diagnostics;
using System.ComponentModel;
using System.IO;

namespace YMMProjectManager.Infrastructure.Git;

public sealed partial class GitRepository
{
    internal Action<CancellationToken>? BeforePullExecutionRecheck { get; set; }
    internal Action? AfterPullJournalCreated { get; set; }
    internal Action<Process>? AfterPullProcessStarted { get; set; }
    internal Action? AfterPullProcessExit { get; set; }

    public Task<GitResult> InspectPullRemoteAsync(CancellationToken token = default) =>
        InspectPullRemoteAsync(GitHubAuthentication.Anonymous, token);

    public Task<GitResult> InspectPullRemoteAsync(GitHubAuthentication authentication, CancellationToken token = default) =>
        WithinRemoteDeadlineAsync("Pull preview", async deadline =>
        {
            if (HasPendingPullEvidence()) return UnresolvedPull();
            using var session = await OpenPullSessionAsync(deadline).ConfigureAwait(false);
            if (session is null) return UnsupportedPullRepository();
            if (GitPullOperationJournal.Exists(directory)) return UnresolvedPull();
            var (remote, failure) = await ReadPullRemoteAsync(session, deadline, authentication).ConfigureAwait(false);
            return failure ?? new GitResult(0, "", "") { PullRemote = remote };
        }, token, RemoteTimeout);

    public Task<GitResult> InspectPullTargetAsync(GitPullRemote approvedRemote, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(approvedRemote);
        return WithinRemoteDeadlineAsync("Pull preview", async deadline =>
        {
            if (HasPendingPullEvidence()) return UnresolvedPull();
            using var session = await OpenPullSessionAsync(deadline).ConfigureAwait(false);
            if (session is null) return UnsupportedPullRepository();
            if (GitPullOperationJournal.Exists(directory)) return UnresolvedPull();
            var (current, failure) = await ReadPullRemoteAsync(session, deadline, approvedRemote.Authentication).ConfigureAwait(false);
            if (failure is not null) return failure;
            if (!SamePullRemote(current!, approvedRemote)) return ChangedPull();
            var (target, error) = await ReadAdvertisedCommitAsync(session, current!, deadline).ConfigureAwait(false);
            if (error is not null) return error;
            return new GitResult(0, "", "") { PullTarget = new GitPullTarget(current!, target!) };
        }, token, RemoteTimeout);
    }

    public Task<GitResult> PullTrustedAsync(GitPullTarget target, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return WithinRemoteDeadlineAsync("Pull", deadline =>
            WithAppWriteLeaseAsync(() => PullConfirmedCoreAsync(target, deadline), deadline), token, RemoteTimeout);
    }

    private async Task<GitResult> PullConfirmedCoreAsync(GitPullTarget approved, CancellationToken preparationToken)
    {
        if (HasPendingPullEvidence()) return UnresolvedPull();
        using var session = await OpenPullSessionAsync(preparationToken).ConfigureAwait(false);
        if (session is null) return UnsupportedPullRepository();
        if (GitPullOperationJournal.Exists(directory)) return UnresolvedPull();
        var (remote, failure) = await ReadPullRemoteAsync(session, preparationToken, approved.Remote.Authentication).ConfigureAwait(false);
        if (failure is not null) return failure;
        if (!SamePullRemote(remote!, approved.Remote)) return ChangedPull();

        var (advertised, error) = await ReadAdvertisedCommitAsync(session, approved.Remote, preparationToken).ConfigureAwait(false);
        if (error is not null) return error;
        if (advertised != approved.TargetObjectId) return ChangedPull();
        if (!approved.ChangesWorktree)
        {
            var (_, unchangedError) = await ReadPreparedPullAsync(session, approved, preparationToken).ConfigureAwait(false);
            return unchangedError ?? new GitResult(0, "", "The approved branch, index and checkout are already up to date.");
        }

        var imported = await RunPullTransportAsync(session, approved.Endpoint,
            ["fetch", "--no-recurse-submodules", "--no-tags", "--no-prune", "--no-prune-tags",
                "--no-auto-maintenance", "--no-write-fetch-head", "--refmap=", "--", approved.Endpoint, approved.TargetObjectId],
            preparationToken, approved.Remote.Authentication).ConfigureAwait(false);
        if (!imported.Success) return SanitizeRemote(imported, classifyPushStatus: false, cloneStage: null);

        BeforePullExecutionRecheck?.Invoke(preparationToken);
        var (latest, remoteError) = await ReadAdvertisedCommitAsync(session, approved.Remote, preparationToken).ConfigureAwait(false);
        if (remoteError is not null) return remoteError;
        if (latest != approved.TargetObjectId) return ChangedPull();

        var (before, preflightError) = await ReadPreparedPullAsync(session, approved, preparationToken).ConfigureAwait(false);
        if (preflightError is not null) return preflightError;
        var ancestry = await ClassifyPullAsync(approved.StartObjectId, approved.TargetObjectId, preparationToken, session.Settings).ConfigureAwait(false);
        if (ancestry != GitPullState.FastForwardCandidate)
            return ancestry is GitPullState.Diverged or GitPullState.LocalAhead
                ? new GitResult(1, "", "The approved commit is not a fast-forward update.", GitFailure.Divergence)
                : UnsafeRemote("Could not verify a fast-forward history for the approved commit.");

        if (await InspectIgnoredPathsAsync(approved.StartObjectId, approved.TargetObjectId, preparationToken, session.Settings).ConfigureAwait(false)
            != GitPullState.FastForwardCandidate)
            return UnsafeRemote("An ignored path overlaps the incoming changes or the paths could not be verified.");

        var paths = await VerifyIncomingPullPathsAsync(session, approved, preparationToken).ConfigureAwait(false);
        if (paths is not null) return paths;

        preparationToken.ThrowIfCancellationRequested();
        var evidence = new GitPullOperationJournal.ApprovalEvidence(approved.BranchRef, approved.StartObjectId,
            approved.RemoteName, approved.UpstreamRef, approved.TargetObjectId);
        GitPullOperationJournal journal;
        try { journal = GitPullOperationJournal.Create(directory, evidence); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GitPullOperationJournal.Exists(directory) ? UnresolvedPull()
                : UnsafeRemote("Could not persist a recovery record. Pull did not start.");
        }

        using (journal)
        {
            var launchAttempted = false;
            try
            {
                AfterPullJournalCreated?.Invoke();
                var (rechecked, stateError) = await ReadPreparedPullAsync(session, approved, preparationToken).ConfigureAwait(false);
                if (stateError is not null || rechecked != before)
                    return ClearPullJournal(journal, stateError ?? ChangedPull());
                preparationToken.ThrowIfCancellationRequested();

                launchAttempted = true;
                // From this point user cancellation does not kill Git during the checkout update.
                // A separate bounded command deadline still stops a hung process.
                using var execution = new CancellationTokenSource(RemoteTimeout);
                GitResult? command = null;
                try
                {
                    command = await RunPullCommandAsync(session,
                        ["merge", "--ff-only", "--no-overwrite-ignore", "--no-autostash", "--no-edit",
                            "--no-stat", "--no-verify-signatures", "--", approved.TargetObjectId], execution.Token, AfterPullProcessStarted).ConfigureAwait(false);
                    AfterPullProcessExit?.Invoke();
                }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or Win32Exception or InvalidOperationException)
                {
                    // An attempted command is never assumed rolled back.
                }

                using var verification = new CancellationTokenSource(RemoteTimeout);
                var after = await ReadCurrentPullStateAsync(session, approved, verification.Token).ConfigureAwait(false);
                if (after is null) return UnresolvedPull();
                if (command is { Success: true } && after.StartObjectId == approved.TargetObjectId &&
                    after.Branches == ReplaceBranchObjectId(before!.Branches, approved.BranchRef, approved.TargetObjectId))
                    return ClearPullJournal(journal, new GitResult(0, "", "The approved fast-forward and checkout were verified."));
                if (command is { Success: false } && after.StartObjectId == approved.StartObjectId &&
                    after.Branches == before!.Branches && after.Index == before.Index)
                    return ClearPullJournal(journal, UnsafeRemote("Git rejected the update. The branch, index and checkout were verified unchanged."));
                return UnresolvedPull();
            }
            catch (OperationCanceledException) when (!launchAttempted)
            {
                if (!journal.Clear()) return UnresolvedPull();
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or Win32Exception or InvalidOperationException)
            {
                if (launchAttempted) return UnresolvedPull();
                return ClearPullJournal(journal, UnsafeRemote("Pull was stopped before the checkout update could start."));
            }
        }
    }

    private GitResult ClearPullJournal(GitPullOperationJournal journal, GitResult result) =>
        journal.Clear() ? result : UnresolvedPull();

    private async Task<(GitPullRemote? Remote, GitResult? Error)> ReadPullRemoteAsync(PullSession session, CancellationToken token,
        GitHubAuthentication authentication)
    {
        var (branch, error) = await ReadBranchTipAsync(trusted: true, token, session.Settings).ConfigureAwait(false);
        if (branch is null || error is not null)
            return (null, UnsafeRemote("The repository must have a checked-out local branch and commit."));
        var gitDirectory = Path.Combine(directory, ".git");
        if (!CanUsePullMetadataFile(gitDirectory, branch.BranchRef) ||
            !CanUsePullMetadataFile(gitDirectory, "logs/" + branch.BranchRef))
            return (null, UnsafeRemote("The selected branch metadata or its reflog uses an unsupported path."));
        var name = branch.BranchRef[BranchRefPrefix.Length..];
        var remote = await ReadSinglePullSettingAsync(session, $"branch.{name}.remote", token).ConfigureAwait(false);
        var upstream = await ReadSinglePullSettingAsync(session, $"branch.{name}.merge", token).ConfigureAwait(false);
        if (remote is null || remote is "" or "." || remote.StartsWith('-') || HasHiddenText(remote) ||
            upstream is null || !upstream.StartsWith(BranchRefPrefix, StringComparison.Ordinal) ||
            !(await RunPullCommandAsync(session, ["check-ref-format", upstream], token).ConfigureAwait(false)).Success)
            return (null, UnsafeRemote("Configure exactly one remote upstream for this checked-out branch."));
        var endpoint = await ReadSinglePullSettingAsync(session, $"remote.{remote}.url", token).ConfigureAwait(false);
        try { ValidateRemote(endpoint ?? ""); }
        catch (ArgumentException) { return (null, UnsupportedPullRemote()); }
        if (!Enum.IsDefined(authentication) ||
            ((HasGitHubHost(endpoint!) || authentication != GitHubAuthentication.Anonymous) && !IsSupportedGitHubEndpoint(endpoint!)))
            return (null, UnsupportedGitHubEndpoint());
        if (Path.IsPathFullyQualified(endpoint!) &&
            (!OnFixedLocalNtfs(endpoint!) || !IsOrdinaryDirectory(endpoint!)))
            return (null, UnsupportedPullRemote());
        return (new GitPullRemote(directory, branch.BranchRef, branch.ObjectId, remote, upstream, endpoint!, session.Identity, session.Settings,
            authentication), null);
    }

    private async Task<string?> ReadSinglePullSettingAsync(PullSession session, string name, CancellationToken token)
    {
        var value = await RunPullCommandAsync(session, ["config", "--local", "--no-includes", "-z", "--get-all", name], token).ConfigureAwait(false);
        return value.Success && value.StandardOutput.EndsWith('\0') && value.StandardOutput.IndexOf('\0') == value.StandardOutput.Length - 1
            ? value.StandardOutput[..^1] : null;
    }

    private async Task<(string? ObjectId, GitResult? Error)> ReadAdvertisedCommitAsync(PullSession session, GitPullRemote remote, CancellationToken token)
    {
        var listing = await RunPullTransportAsync(session, remote.Endpoint,
            ["ls-remote", "--exit-code", "--refs", "--", remote.Endpoint, remote.UpstreamRef], token, remote.Authentication).ConfigureAwait(false);
        if (listing.ExitCode == 2)
            return (null, UnsafeRemote("The approved upstream branch is missing."));
        if (!listing.Success) return (null, SanitizeRemote(listing, classifyPushStatus: false, cloneStage: null));
        var fields = listing.StandardOutput.TrimEnd('\r', '\n').Split('\t');
        return fields.Length == 2 && IsObjectId(fields[0]) && fields[1] == remote.UpstreamRef
            ? (fields[0], null)
            : (null, UnsafeRemote("The remote did not report a unique commit for the selected upstream."));
    }

    private sealed record PullSnapshot(string StartObjectId, string Index, string Branches);

    private async Task<(PullSnapshot? State, GitResult? Error)> ReadPreparedPullAsync(PullSession session, GitPullTarget approved, CancellationToken token)
    {
        var (current, failure) = await ReadPullRemoteAsync(session, token, approved.Remote.Authentication).ConfigureAwait(false);
        if (failure is not null || !SamePullRemote(current!, approved.Remote)) return (null, ChangedPull());
        var state = await ReadCurrentPullStateAsync(session, approved, token).ConfigureAwait(false);
        return state is not null && state.StartObjectId == approved.StartObjectId
            ? (state, null)
            : (null, UnsafeRemote("Pull requires the approved checked-out branch, a clean index and worktree, and no unfinished Git operation."));
    }

    private async Task<PullSnapshot?> ReadCurrentPullStateAsync(PullSession session, GitPullTarget approved, CancellationToken token)
    {
        using var observed = GitRepositoryLease.TryOpen(directory);
        if (observed?.Identity != approved.Identity) return null;
        var (branch, _) = await ReadBranchTipAsync(trusted: true, token, session.Settings).ConfigureAwait(false);
        if (branch is null || branch.BranchRef != approved.BranchRef) return null;
        var worktree = await ReadPullWorktreeAsync(branch.BranchRef, tracking: null, token, session.Settings).ConfigureAwait(false);
        if (worktree.Blocked is not null || worktree.Status != "") return null;
        var index = await RunPullCommandAsync(session, ["diff-index", "--cached", "--quiet", "--ignore-submodules=none", branch.ObjectId, "--"], token)
            .ConfigureAwait(false);
        if (!index.Success || index.StandardError.Length != 0) return null;
        var refs = await RunPullCommandAsync(session, ["for-each-ref", "--sort=refname", "--format=%(refname)%09%(objectname)", "--", "refs/heads"], token)
            .ConfigureAwait(false);
        if (!refs.Success || refs.StandardError.Length != 0 ||
            !refs.StandardOutput.Split('\n').Contains(branch.BranchRef + "\t" + branch.ObjectId, StringComparer.Ordinal))
            return null;
        var (last, _) = await ReadBranchTipAsync(trusted: true, token, session.Settings).ConfigureAwait(false);
        return last == branch ? new PullSnapshot(branch.ObjectId, worktree.Index!, refs.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal)) : null;
    }

    private static string ReplaceBranchObjectId(string branches, string reference, string objectId)
    {
        var prefix = reference + "\t";
        return string.Join('\n', branches.Split('\n').Select(line => line.StartsWith(prefix, StringComparison.Ordinal) ? prefix + objectId : line));
    }

    private static bool SamePullRemote(GitPullRemote current, GitPullRemote approved) =>
        current.RepositoryRoot == approved.RepositoryRoot && current.BranchRef == approved.BranchRef &&
        current.StartObjectId == approved.StartObjectId && current.RemoteName == approved.RemoteName &&
        current.UpstreamRef == approved.UpstreamRef && current.Endpoint == approved.Endpoint && current.Authentication == approved.Authentication &&
        current.Identity == approved.Identity && current.Settings.SequenceEqual(approved.Settings, StringComparer.Ordinal);

    private static GitResult UnsupportedPullRepository() =>
        UnsafeRemote("Pull requires a verified, direct repository without unsupported Git configuration.");
    private static GitResult UnsupportedPullRemote() =>
        UnsafeRemote("Pull supports a configured anonymous HTTPS endpoint or a regular local Git remote.");
    private static GitResult ChangedPull() =>
        UnsafeRemote("The approved repository, branch, configuration or remote commit changed. Inspect and confirm the Pull again.");

    private bool HasPendingPullEvidence() =>
        !HasHiddenText(directory) && OnFixedLocalNtfs(directory) && IsOrdinaryDirectory(Path.Combine(directory, ".git")) &&
        GitPullOperationJournal.Exists(directory);
    private GitResult UnresolvedPull() =>
        new GitResult(1, "", "The outcome could not be verified. Keep the operation record and review the repository before using Pull again.",
            GitFailure.OutcomeUnknown) { RecoveryPath = GitPullOperationJournal.GetPath(directory) };

    private async Task<GitResult> WithAppWriteLeaseAsync(Func<Task<GitResult>> run, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var lease = GitAppWriteLease.TryAcquire(directory);
        return lease is null ? new GitResult(1, "", "Another Git write is in progress.", GitFailure.ConcurrentOperation)
            : await run().ConfigureAwait(false);
    }
}

public sealed class GitPullRemote
{
    internal GitPullRemote(string repositoryRoot, string branchRef, string startObjectId, string remoteName,
        string upstreamRef, string endpoint, GitRepositoryIdentity identity, string[] settings,
        GitHubAuthentication authentication = GitHubAuthentication.Anonymous)
    {
        RepositoryRoot = repositoryRoot;
        BranchRef = branchRef;
        StartObjectId = startObjectId;
        RemoteName = remoteName;
        UpstreamRef = upstreamRef;
        Endpoint = endpoint;
        Authentication = authentication;
        Identity = identity;
        Settings = settings.ToArray();
    }

    public string RepositoryRoot { get; }
    public string BranchRef { get; }
    public string StartObjectId { get; }
    public string RemoteName { get; }
    public string UpstreamRef { get; }
    public string Endpoint { get; }
    public GitHubAuthentication Authentication { get; }
    internal GitRepositoryIdentity Identity { get; }
    internal string[] Settings { get; }
}

public sealed class GitPullTarget
{
    internal GitPullTarget(GitPullRemote remote, string targetObjectId)
    {
        Remote = remote;
        TargetObjectId = targetObjectId;
    }

    public string RepositoryRoot => Remote.RepositoryRoot;
    public string BranchRef => Remote.BranchRef;
    public string StartObjectId => Remote.StartObjectId;
    public string RemoteName => Remote.RemoteName;
    public string UpstreamRef => Remote.UpstreamRef;
    public string Endpoint => Remote.Endpoint;
    public string TargetObjectId { get; }
    public bool ChangesWorktree => StartObjectId != TargetObjectId;
    internal GitPullRemote Remote { get; }
    internal GitRepositoryIdentity Identity => Remote.Identity;
}
