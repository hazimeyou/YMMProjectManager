using System.ComponentModel;
using System.IO;

namespace YMMProjectManager.Infrastructure.Git;

public sealed partial class GitRepository
{
    internal Action? BeforeCommitRefUpdate { get; init; }

    public Task<GitResult> InspectCommitAsync(CancellationToken token = default) =>
        WithAppWriteLeaseAsync(() => RunCommitCoreAsync(null, null, token), token);

    public Task<GitResult> CommitAsync(GitCommitTarget target, string message, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return WithAppWriteLeaseAsync(() => RunCommitCoreAsync(target, message, token), token);
    }

    private async Task<GitResult> RunCommitCoreAsync(GitCommitTarget? approved, string? message, CancellationToken token)
    {
        if (message is not null && (message.Length > 32768 || message.Contains('\0')))
            return UnsafeRemote("The commit message contains invalid data or exceeds 32,768 characters.");
        if (HasPendingPullEvidence()) return UnresolvedPull();
        using var session = await OpenPullSessionAsync(token, requireIndex: false).ConfigureAwait(false);
        if (session is null)
            return UnsafeRemote("Commit requires a supported main repository on a fixed local NTFS volume.");
        if (approved is not null && (approved.RepositoryRoot != directory || approved.Identity != session.Identity))
            return UnsafeRemote("The approved repository changed. Review the staged changes again.");

        var gitDirectory = Path.Combine(directory, ".git");
        try
        {
            if (PullOperationMarkers.Any(marker => HasGitEntry(Path.Combine(gitDirectory, marker))))
                return BusyIndex();
            // Other Git commands must acquire index.lock to change staged state or check
            // out another branch. Keep HEAD itself open to also stop symbolic-ref changes.
            using var indexLock = new FileStream(Path.Combine(gitDirectory, "index.lock"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            using var headFile = ProjectListReplacement.Open(Path.Combine(gitDirectory, "HEAD"));
            using var indexFile = ProjectListReplacement.TryOpen(Path.Combine(gitDirectory, "index"));
            if (indexFile is null) return UnsafeRemote("Stage at least one file before committing.");

            var head = await RunPullCommandAsync(session, ["symbolic-ref", "--quiet", "--no-recurse", "HEAD"], token).ConfigureAwait(false);
            var branch = head.StandardOutput.TrimEnd('\r', '\n');
            if (!head.Success || !branch.StartsWith(BranchRefPrefix, StringComparison.Ordinal) || HasHiddenText(branch) ||
                !(await RunPullCommandAsync(session, ["check-ref-format", branch], token).ConfigureAwait(false)).Success ||
                !CanUsePullMetadataFile(gitDirectory, branch) ||
                !CanUsePullMetadataFile(gitDirectory, "logs/" + branch))
                return UnsafeRemote("Commit requires an ordinary checked-out local branch.");

            var symbolicBranch = await RunPullCommandAsync(session, ["symbolic-ref", "--quiet", "--no-recurse", branch], token).ConfigureAwait(false);
            if (symbolicBranch.ExitCode != 1 || symbolicBranch.StandardOutput.Length != 0 || symbolicBranch.StandardError.Length != 0)
                return UnsafeRemote("The selected branch is symbolic or its identity cannot be verified.");
            var current = await RunPullCommandAsync(session, ["rev-parse", "--verify", "--quiet", branch + "^{commit}"], token).ConfigureAwait(false);
            var parent = current.Success ? current.StandardOutput.Trim() : null;
            if (parent is not null ? !IsObjectId(parent) : current.ExitCode != 1 || current.StandardError.Length != 0)
                return UnsafeRemote("Could not verify the parent commit.");

            // write-tree may refresh cache-tree metadata. Give it an owned copy so the
            // user's index bytes stay exactly as staged, even when validation fails.
            var snapshotPath = Path.Combine(gitDirectory, "ymm-index-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (var snapshot = new FileStream(snapshotPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await indexFile.CopyToAsync(snapshot, token).ConfigureAwait(false);
                var treeResult = await RunDirectAsync(["write-tree"], token, pullSettings: session.Settings,
                    privateIndex: snapshotPath).ConfigureAwait(false);
                var tree = treeResult.StandardOutput.Trim();
                if (!treeResult.Success || !IsObjectId(tree))
                    return UnsafeRemote("The staged tree is unavailable. Resolve index conflicts before committing.");
                var changes = parent is null
                    ? new[] { "ls-tree", "-r", "--full-tree", "--name-only", "-z", tree }
                    : new[] { "diff-tree", "--no-commit-id", "--name-only", "-r", "-z", "--no-renames", parent, tree, "--" };
                var staged = await RunDirectAsync(changes, token, pullSettings: session.Settings,
                    outputLimit: 1024 * 1024).ConfigureAwait(false);
                if (!staged.Success || !staged.StandardOutput.EndsWith('\0'))
                    return UnsafeRemote("No staged changes could be verified.");
                var target = new GitCommitTarget(directory, branch, parent, tree,
                    Array.AsReadOnly(staged.StandardOutput.Split('\0')[..^1]), session.Identity);
                if (approved is not null && (approved.BranchRef != branch || approved.ParentObjectId != parent || approved.TreeObjectId != tree))
                    return UnsafeRemote("The staged tree or parent changed. Review the staged changes again.");
                if (message is null) return new GitResult(0, "", "") { CommitTarget = target };

                if (!HasOrdinaryPublicationObjectStore())
                    return PublicationRefusal("This Git object store cannot be inspected safely.");
                using var registry = OpenAssetRegistryLease();
                var mediaError = await CheckPublicationTreeAsync(tree, token).ConfigureAwait(false);
                if (mediaError is not null) return mediaError;
                var identitySettings = await ReadCommitIdentityAsync(token).ConfigureAwait(false);
                if (identitySettings is null)
                    return UnsafeRemote("Configure a Git user.name and user.email before committing.");
                var args = new List<string>(identitySettings) { "commit-tree", "--no-gpg-sign", tree };
                if (parent is not null) args.AddRange(["-p", parent]);
                args.AddRange(["-F", "-"]);
                var commit = await RunDirectAsync(args.ToArray(), token, pullSettings: session.Settings,
                    standardInput: message + "\n").ConfigureAwait(false);
                var objectId = commit.StandardOutput.Trim();
                if (!commit.Success || !IsObjectId(objectId))
                    return UnsafeRemote("Git could not create the reviewed commit object. Check Git identity and repository configuration.");

                BeforeCommitRefUpdate?.Invoke();
                token.ThrowIfCancellationRequested();
                return await PublishCommitRefAsync(session, branch, parent ?? new string('0', tree.Length), objectId).ConfigureAwait(false);
            }
            finally
            {
                TryCleanCommitIndex(snapshotPath);
            }
        }
        catch (IOException) { return BusyIndex(); }
        catch (UnauthorizedAccessException) { return BusyIndex(); }
        catch (TimeoutException) { return new(1, "", "Commit preparation timed out.", GitFailure.Timeout); }
        catch (Win32Exception) { return UnsafeRemote("Git could not be started for Commit."); }
    }

    private async Task<GitResult> PublishCommitRefAsync(PullSession session, string branch, string parent, string commit)
    {
        try
        {
            // From here native Git owns the branch CAS. Late UI cancellation cannot
            // change the reported result of a published commit.
            var update = await RunPullCommandAsync(session, ["update-ref", "--no-deref", "-m", "YMMProjectManager commit",
                branch, commit, parent], CancellationToken.None).ConfigureAwait(false);
            var observed = await RunPullCommandAsync(session, ["rev-parse", "--verify", "--quiet", branch + "^{commit}"],
                CancellationToken.None).ConfigureAwait(false);
            if (observed.Success && observed.StandardOutput.Trim() == commit)
                return new GitResult(0, commit, "");
            if (!update.Success)
                return new(1, "", "Git refused the branch update. Refresh Git state before retrying.", GitFailure.ConcurrentOperation);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or Win32Exception or InvalidOperationException)
        {
            // The command can have updated the ref before its result became unavailable.
        }
        return new(1, "", "The Commit outcome could not be verified. Inspect branch history before retrying.", GitFailure.OutcomeUnknown);
    }

    private async Task<string[]?> ReadCommitIdentityAsync(CancellationToken token)
    {
        var settings = new List<string>();
        foreach (var key in new[] { "user.name", "user.email" })
        {
            var value = await RunDirectAsync(["config", "-z", "--get", key], token, outputLimit: 2048).ConfigureAwait(false);
            if (!value.Success && value.ExitCode == 1 && value.StandardOutput.Length == 0 && value.StandardError.Length == 0)
                continue; // Git may have a usable OS identity.
            if (!value.Success || value.StandardError.Length != 0 || !value.StandardOutput.EndsWith('\0') ||
                HasHiddenText(value.StandardOutput[..^1])) return null;
            settings.Add("-c");
            settings.Add(key + "=" + value.StandardOutput[..^1]);
        }
        return settings.ToArray();
    }

    private static GitResult BusyIndex() =>
        new(1, "", "Git index or repository state is busy. Finish any other Git operation, then review the staged changes.",
            GitFailure.ConcurrentOperation);

    private static void TryCleanCommitIndex(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class GitCommitTarget
{
    internal GitCommitTarget(string repositoryRoot, string branchRef, string? parentObjectId, string treeObjectId,
        IReadOnlyList<string> paths, GitRepositoryIdentity identity)
    {
        RepositoryRoot = repositoryRoot;
        BranchRef = branchRef;
        ParentObjectId = parentObjectId;
        TreeObjectId = treeObjectId;
        Paths = paths;
        Identity = identity;
    }

    public string RepositoryRoot { get; }
    public string BranchRef { get; }
    public string? ParentObjectId { get; }
    public string TreeObjectId { get; }
    public IReadOnlyList<string> Paths { get; }
    internal GitRepositoryIdentity Identity { get; }
}
