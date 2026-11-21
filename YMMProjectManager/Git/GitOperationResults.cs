namespace YMMProjectManager.Infrastructure.Git;

public enum GitFailure { None, Authentication, Permission, Network, Divergence, Unknown, UnsafeOperation, Timeout, PathTooLong, ConcurrentOperation, OutcomeUnknown, MediaPublication, RemoteRedirect }
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
    public GitPullRemote? PullRemote { get; init; }
    public GitPullTarget? PullTarget { get; init; }
    public GitCommitTarget? CommitTarget { get; init; }
}
