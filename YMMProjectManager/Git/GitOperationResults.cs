namespace YMMProjectManager.Infrastructure.Git;

//CSJ,enum=GitFailure,Git失敗
public enum GitFailure { None, Authentication, Permission, Network, Divergence, Unknown, UnsafeOperation, Timeout, PathTooLong, ConcurrentOperation, OutcomeUnknown, MediaPublication, RemoteRedirect }
//CSJ,enum=GitRemoteOperation,Gitリモート操作
public enum GitRemoteOperation { Fetch, Push }
//CSJ,enum=GitPullState,Gitプル状態
public enum GitPullState { Unavailable, TrackingUnavailable, Dirty, UpToDate, FastForwardCandidate, LocalAhead, Diverged, Changed, OperationInProgress, IndexNotVerifiable, IgnoredPathCollision, HistoryNotVerifiable }
//CSJ,record=GitPullAssessment,Gitプル評価
public sealed record GitPullAssessment(GitPullState State, string? BranchRef = null, string? LocalObjectId = null,
    string? TrackingRef = null, string? TrackingObjectId = null, string? Message = null);
//CSJ,record=GitRemoteDiagnostic,Gitリモート診断
public sealed record GitRemoteDiagnostic(string Operation, TimeSpan Elapsed);

//CSJ,class=GitFetchTarget,Gitフェッチ対象
public sealed class GitFetchTarget
{
    internal GitFetchTarget(string repositoryRoot, string endpoint, GitRepositoryIdentity identity)
    {
        RepositoryRoot = repositoryRoot;
        Endpoint = endpoint;
        Identity = identity;
    }

    //CSJ,property=RepositoryRoot,リポジトリルート
    public string RepositoryRoot { get; }
    //CSJ,property=Endpoint,エンドポイント
    public string Endpoint { get; }
    //CSJ,property=Identity,識別情報
    internal GitRepositoryIdentity Identity { get; }
}

//CSJ,record=GitPushTarget,Gitプッシュ対象
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

    //CSJ,property=RepositoryRoot,リポジトリルート
    public string RepositoryRoot { get; }
    //CSJ,property=Endpoint,エンドポイント
    public string Endpoint { get; }
    //CSJ,property=BranchRef,ブランチ参照
    public string BranchRef { get; }
    //CSJ,property=ObjectId,オブジェクトID
    public string ObjectId { get; }
    //CSJ,property=Identity,識別情報
    internal GitRepositoryIdentity? Identity { get; }
}

//CSJ,record=GitResult,Git結果
public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError, GitFailure Failure = GitFailure.None)
{
    //CSJ,property=Success,成功
    public bool Success => ExitCode == 0;
    //CSJ,property=RecoveryPath,復旧パス
    public string? RecoveryPath { get; init; }
    //CSJ,property=RemoteDiagnostic,リモート診断
    public GitRemoteDiagnostic? RemoteDiagnostic { get; init; }
    //CSJ,property=PushTarget,プッシュ対象
    public GitPushTarget? PushTarget { get; init; }
    //CSJ,property=FetchTarget,フェッチ対象
    public GitFetchTarget? FetchTarget { get; init; }
    //CSJ,property=PullRemote,プルリモート
    public GitPullRemote? PullRemote { get; init; }
    //CSJ,property=PullTarget,プル対象
    public GitPullTarget? PullTarget { get; init; }
    //CSJ,property=CommitTarget,コミット対象
    public GitCommitTarget? CommitTarget { get; init; }
}
