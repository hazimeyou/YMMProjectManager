using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace YMMProjectManager.Infrastructure.Git;

/// <summary>Coordinates local and remote Git operations against the selected repository.</summary>
public sealed partial class GitRepository
{
    //CSJ,field=Timeout,タイムアウト
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    //CSJ,field=BranchRefPrefix,ブランチ参照接頭辞
    private const string BranchRefPrefix = "refs/heads/";
    private static readonly string[] PullOperationMarkers =
    [
        "HEAD.lock", "ORIG_HEAD.lock", "index.lock", "packed-refs.lock",
        "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "BISECT_START",
        "rebase-apply", "rebase-merge", "sequencer",
    ];
    //CSJ,field=directory,ディレクトリ
    private readonly string directory;
    internal Action<CancellationToken>? BeforeRemoteExecution { get; set; }
    internal Action? BeforePullInspectionRecheck { get; set; }
    //CSJ,property=RemoteTimeout,リモートタイムアウト
    internal TimeSpan RemoteTimeout { get; init; } = Timeout;

    public GitRepository(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(this.directory))
            throw new DirectoryNotFoundException(this.directory);
    }

    //CSJ,method=InitializeAsync,初期化する（非同期）
    public Task<GitResult> InitializeAsync(CancellationToken token = default) => WithAppWriteLeaseAsync(() => RunAsync(["init"], token), token);
    //CSJ,method=DetectAsync,検出する（非同期）
    public Task<GitResult> DetectAsync(CancellationToken token = default) => RunAsync(["rev-parse", "--show-toplevel"], token);
    //CSJ,method=StatusAsync,状態（非同期）
    public Task<GitResult> StatusAsync(CancellationToken token = default) => RunAsync(["--no-optional-locks", "status", "--porcelain=v1", "-z"], token);
    //CSJ,method=HistoryAsync,履歴（非同期）
    public Task<GitResult> HistoryAsync(CancellationToken token = default) => RunAsync(["log", "-n", "100", "--encoding=UTF-8", "--format=%H%x00%an%x00%aI%x00%s"], token);
    //CSJ,method=BranchesAsync,ブランチ一覧（非同期）
    public Task<GitResult> BranchesAsync(CancellationToken token = default) => RunAsync(["branch", "--list", "--no-color"], token);
    //CSJ,method=OriginAsync,originのURLを取得する（非同期）
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
    //CSJ,method=StageAsync,変更をステージする（非同期）
    public Task<GitResult> StageAsync(IReadOnlyCollection<string> paths, CancellationToken token = default) =>
        WithAppWriteLeaseAsync(() => RunIndexChangeAsync(paths, stage: true, token), token);
    //CSJ,method=UnstageAsync,ステージ済みの変更を解除する（非同期）
    public Task<GitResult> UnstageAsync(IReadOnlyCollection<string> paths, CancellationToken token = default) =>
        WithAppWriteLeaseAsync(() => RunIndexChangeAsync(paths, stage: false, token), token);
    //CSJ,method=CommitAsync,コミット（非同期）
    public Task<GitResult> CommitAsync(string message, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return WithAppWriteLeaseAsync(() => RunCommitCoreAsync(null, message, token), token);
    }
    //CSJ,method=CreateBranchAsync,ブランチを作成する（非同期）
    public Task<GitResult> CreateBranchAsync(string name, CancellationToken token = default) => WithAppWriteLeaseAsync(() => RunBranchAsync(["branch"], name, token), token);
    //CSJ,method=SwitchBranchAsync,ブランチを切り替える（非同期）
    public Task<GitResult> SwitchBranchAsync(string name, CancellationToken token = default) => WithAppWriteLeaseAsync(() => RunBranchAsync(["switch", "--no-overwrite-ignore"], name, token), token);

    //CSJ,method=InspectRemoteAsync,リモートを点検する（非同期）
    public Task<GitResult> InspectRemoteAsync(GitRemoteOperation operation, CancellationToken token = default) =>
        WithinRemoteDeadlineAsync(operation.ToString(), async deadline =>
        {
            if (operation is not (GitRemoteOperation.Fetch or GitRemoteOperation.Push))
                throw new ArgumentOutOfRangeException(nameof(operation));
            if (!HasDirectRepository()) return UnsafeRemote("Select the main repository root on a regular filesystem path.");
            //CSJ,variable=lease,リース
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

    //CSJ,method=FetchAsync,フェッチする（非同期）
    public Task<GitResult> FetchAsync(CancellationToken token = default) =>
        WithinRemoteDeadlineAsync("Fetch", deadline => WithAppWriteLeaseAsync(() => FetchCoreAsync(null, deadline), deadline), token, RemoteTimeout);

    //CSJ,method=FetchTrustedAsync,信頼済みをフェッチする（非同期）
    public Task<GitResult> FetchTrustedAsync(string expectedRemote, CancellationToken token = default)
    {
        ValidateRemote(expectedRemote);
        return WithinRemoteDeadlineAsync("Fetch", deadline => WithAppWriteLeaseAsync(() => FetchCoreAsync(expectedRemote, deadline), deadline), token, RemoteTimeout);
    }

    //CSJ,method=FetchTrustedAsync,信頼済みをフェッチする（非同期）
    public Task<GitResult> FetchTrustedAsync(GitFetchTarget target, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return WithinRemoteDeadlineAsync("Fetch", deadline => WithAppWriteLeaseAsync(() => FetchCoreAsync(target.Endpoint, deadline, target), deadline), token, RemoteTimeout);
    }

    //CSJ,method=FetchCoreAsync,中核をフェッチする（非同期）
    private async Task<GitResult> FetchCoreAsync(string? expectedRemote, CancellationToken token, GitFetchTarget? approvedTarget = null,
        //CSJ,parameter=authentication,認証
        GitHubAuthentication authentication = GitHubAuthentication.Anonymous)
    {
        //CSJ,variable=trusted,信頼済み
        var trusted = expectedRemote is not null;
        if (trusted && !HasDirectRepository()) return UnsafeRemote("Select the main repository root on a regular filesystem path.");
        if (approvedTarget is not null && !string.Equals(directory, approvedTarget.RepositoryRoot, StringComparison.OrdinalIgnoreCase))
            return UnsafeRemote("This fetch approval belongs to a different repository.");
        //CSJ,variable=lease,リース
        using var lease = approvedTarget is null ? null : GitRepositoryLease.TryOpen(directory);
        if (approvedTarget is not null && lease?.Identity != approvedTarget.Identity)
            return UnsafeRemote("The approved repository or its Git directory changed. Review the fetch target again.");
        var (url, error) = await ResolveOriginAsync(push: false, token, literal: trusted).ConfigureAwait(false);
        if (error is not null) return error;
        if (!CanUseRemote(url!, expectedRemote)) return UnsafeRemote("The origin differs from the approved endpoint or requires explicit trust.");
        BeforeRemoteExecution?.Invoke(token);
        if (trusted && !HasDirectRepository()) return UnsafeRemote("Repository path changed before fetch.");
        return await RunRemoteAsync(url!, ["fetch", "--no-recurse-submodules", "--", url!, "+refs/heads/*:refs/remotes/origin/*"],
            token, trusted, authentication: authentication).ConfigureAwait(false);
    }

    /// <summary>Assesses the locally fetched origin branch without fetching or modifying the repository.</summary>
    //CSJ,method=InspectPullAsync,プルを点検する（非同期）
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

    //CSJ,method=InspectPullCoreAsync,プル中核を点検する（非同期）
    private async Task<GitPullAssessment> InspectPullCoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!HasDirectRepository())
            return new GitPullAssessment(GitPullState.Unavailable, Message: "Select the main repository root on a regular filesystem path.");
        //CSJ,variable=lease,リース
        using var lease = GitRepositoryLease.TryOpen(directory);
        if (lease is null)
            return new GitPullAssessment(GitPullState.Unavailable, Message: "Could not verify the repository and its Git directory identity.");
        var (branch, error) = await ReadBranchTipAsync(trusted: true, token).ConfigureAwait(false);
        if (error is not null) return new GitPullAssessment(GitPullState.Unavailable, Message: error.StandardError);
        //CSJ,variable=tracking,追跡
        var tracking = await ReadPullTrackingAsync(branch!.BranchRef, token).ConfigureAwait(false);
        if (tracking.Error is not null)
            return new GitPullAssessment(GitPullState.Unavailable, branch.BranchRef, branch.ObjectId, Message: tracking.Error);
        //CSJ,variable=worktree,作業ツリー
        var worktree = await ReadPullWorktreeAsync(branch.BranchRef, tracking.Ref, token).ConfigureAwait(false);
        if (worktree.Blocked is { } blocked)
            return new GitPullAssessment(blocked, branch.BranchRef, branch.ObjectId, tracking.Ref, tracking.ObjectId, worktree.Message);

        //CSJ,variable=state,状態
        var state = worktree.Status!.Length != 0 ? GitPullState.Dirty : tracking.ObjectId is null
            ? GitPullState.TrackingUnavailable : await ClassifyPullAsync(branch.ObjectId, tracking.ObjectId, token).ConfigureAwait(false);

        var checkIgnoredPaths = state == GitPullState.FastForwardCandidate;
        if (checkIgnoredPaths)
            state = await InspectIgnoredPathsAsync(branch.ObjectId, tracking.ObjectId!, token).ConfigureAwait(false);

        // The result describes these observations only; no ref or index transaction is held.
        BeforePullInspectionRecheck?.Invoke();
        var (latestBranch, _) = await ReadBranchTipAsync(trusted: true, token).ConfigureAwait(false);
        //CSJ,variable=latestTracking,最新追跡
        var latestTracking = await ReadPullTrackingAsync(branch.BranchRef, token).ConfigureAwait(false);
        //CSJ,variable=latestWorktree,最新作業ツリー
        var latestWorktree = await ReadPullWorktreeAsync(branch.BranchRef, tracking.Ref, token).ConfigureAwait(false);
        if (!HasDirectRepository() || latestBranch != branch || latestTracking != tracking || latestWorktree != worktree)
            return new GitPullAssessment(GitPullState.Changed, Message: "The repository changed during inspection. Refresh the assessment.");

        if (checkIgnoredPaths && await InspectIgnoredPathsAsync(branch.ObjectId, tracking.ObjectId!, token).ConfigureAwait(false) != state)
            return new GitPullAssessment(GitPullState.Changed, Message: "Ignored paths changed during inspection. Refresh the assessment.");

        //CSJ,variable=message,メッセージ
        var message = state == GitPullState.IgnoredPathCollision
            ? "An ignored path overlaps a path in the incoming changes."
            : tracking.Message;
        return new GitPullAssessment(state, branch.BranchRef, branch.ObjectId, tracking.Ref, tracking.ObjectId, message);
    }

    private async Task<GitPullState> InspectIgnoredPathsAsync(string local, string upstream, CancellationToken token, string[]? pullSettings = null)
    {
        // Normal porcelain omits ignored local files. Compare incoming changes with the ignored
        // paths themselves rather than treating all ignored files as a checkout obstruction.
        var ignored = await RunDirectAsync(["ls-files", "-z", "--others", "--ignored", "--exclude-standard"], token, pullSettings: pullSettings).ConfigureAwait(false);
        if (!ignored.Success || ignored.StandardError.Length != 0) return GitPullState.Unavailable;
        if (ignored.StandardOutput.Length == 0) return GitPullState.FastForwardCandidate;

        //CSJ,variable=changed,変更済み
        var changed = await RunDirectAsync(["diff-tree", "-z", "--name-only", "--no-commit-id", "--no-renames",
            "--no-ext-diff", "--no-textconv", "--diff-filter=AMT", "-r", local, upstream, "--"], token, pullSettings: pullSettings).ConfigureAwait(false);
        if (!changed.Success || changed.StandardError.Length != 0) return GitPullState.Unavailable;

        var incoming = changed.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var incomingParents = incoming.SelectMany(ParentPaths).ToHashSet(StringComparer.OrdinalIgnoreCase);
        //CSJ,variable=path,パス
        foreach (var path in ignored.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            token.ThrowIfCancellationRequested();
            //CSJ,variable=file,ファイル
            var file = path.TrimEnd('/');
            if (incoming.Contains(file) || incomingParents.Contains(file) || ParentPaths(file).Any(incoming.Contains))
                return GitPullState.IgnoredPathCollision;
        }
        return GitPullState.FastForwardCandidate;

        //CSJ,method=ParentPaths,親パス一覧
        static IEnumerable<string> ParentPaths(string path)
        {
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                yield return path[..slash];
        }
    }

    //CSJ,record=PullTrackingObservation,プル追跡観測記録
    private sealed record PullTrackingObservation(string? Ref = null, string? ObjectId = null,
        string? Remote = null, string? RemoteRef = null, string? Error = null, string? Message = null);

    //CSJ,method=ReadPullTrackingAsync,プル追跡を読み取る（非同期）
    private async Task<PullTrackingObservation> ReadPullTrackingAsync(string branchRef, CancellationToken token)
    {
        //CSJ,variable=branchName,ブランチ名
        var branchName = branchRef[BranchRefPrefix.Length..];
        //CSJ,variable=remoteSetting,リモート設定
        var remoteSetting = await ReadSettingAsync("remote").ConfigureAwait(false);
        //CSJ,variable=mergeSetting,マージ設定
        var mergeSetting = await ReadSettingAsync("merge").ConfigureAwait(false);
        if (remoteSetting is null || mergeSetting is null)
            return new(Error: "Could not verify the branch tracking configuration.");

        //CSJ,variable=trackingRef,追跡参照
        string trackingRef;
        //CSJ,variable=remote,リモート
        string? remote = null;
        //CSJ,variable=remoteRef,リモート参照
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
            //CSJ,variable=mapping,対応
            var mapping = await RunDirectAsync(
                ["for-each-ref", "--format=%(refname)%00%(upstream)%00%(upstream:remotename)%00%(upstream:remoteref)", "--", branchRef],
                token).ConfigureAwait(false);
            //CSJ,variable=fields,フィールド一覧
            var fields = mapping.StandardOutput.TrimEnd('\r', '\n').Split('\0');
            if (!mapping.Success || fields.Length != 4 || fields[0] != branchRef || fields[2] != remote ||
                fields[3] != remoteRef || !fields[1].StartsWith("refs/remotes/", StringComparison.Ordinal) || HasHiddenText(fields[1]))
                return new(Error: "Could not resolve the configured remote tracking branch.");
            trackingRef = fields[1];
        }

        var result = await RunDirectAsync(["rev-parse", "--verify", "--quiet", trackingRef + "^{commit}"], token).ConfigureAwait(false);
        //CSJ,variable=objectId,オブジェクトID
        var objectId = result.StandardOutput.Trim();
        if (!result.Success && result.ExitCode == 1 && objectId.Length == 0 && result.StandardError.Length == 0)
            return new(trackingRef, Remote: remote, RemoteRef: remoteRef, Message: "There is no locally saved commit for the tracking branch.");
        if (!result.Success || !IsObjectId(objectId))
            return new(Error: "Could not verify the locally saved tracking commit.");
        return new(trackingRef, objectId, remote, remoteRef);

        //CSJ,method=ReadSettingAsync,設定を読み取る（非同期）
        async Task<string[]?> ReadSettingAsync(string key)
        {
            //CSJ,variable=setting,設定
            var setting = await RunDirectAsync(["config", "--null", "--get-all", $"branch.{branchName}.{key}"], token).ConfigureAwait(false);
            if (setting.Success && setting.StandardOutput.EndsWith('\0')) return setting.StandardOutput[..^1].Split('\0');
            if (setting.ExitCode == 1 && setting.StandardOutput.Length == 0 && setting.StandardError.Length == 0) return [];
            return null;
        }
    }

    //CSJ,record=PullWorktreeObservation,プル前の作業ツリー観測結果
    private sealed record PullWorktreeObservation(
        GitPullState? Blocked = null, string? Message = null, string? Index = null, string? Status = null);

    //CSJ,method=ReadPullWorktreeAsync,プル作業ツリーを読み取る（非同期）
    private async Task<PullWorktreeObservation> ReadPullWorktreeAsync(string branch, string? tracking, CancellationToken token, string[]? pullSettings = null)
    {
        try
        {
            //CSJ,variable=gitDirectory,Git ディレクトリ
            var gitDirectory = Path.Combine(directory, ".git");
            if (HasGitEntry(Path.Combine(gitDirectory, "info", "grafts")))
                return new(GitPullState.HistoryNotVerifiable, "Legacy grafts can change the local view of commit ancestry.");

            var markers = PullOperationMarkers.Append(branch + ".lock");
            if (tracking is not null) markers = markers.Append(tracking + ".lock");
            //CSJ,variable=marker,マーカー
            foreach (var marker in markers)
            {
                token.ThrowIfCancellationRequested();
                if (HasGitEntry(Path.Combine(gitDirectory, marker)))
                    return new(GitPullState.OperationInProgress, "A Git lock or unfinished operation is present.");
            }
        }
        catch (IOException) { return new(GitPullState.Unavailable, "Could not verify Git operation markers."); }
        catch (UnauthorizedAccessException) { return new(GitPullState.Unavailable, "Could not verify Git operation markers."); }

        // A read-only diagnosis runs the same constrained status command as confirmed Pull.
        // Keep the configuration handle through this observation, then release it before
        // the caller checks for intervening changes and takes the next observation.
        //CSJ,variable=diagnosticSession,診断セッション
        using var diagnosticSession = pullSettings is null
            ? await OpenPullSessionAsync(token).ConfigureAwait(false)
            : null;
        if (pullSettings is null && diagnosticSession is null)
            return new(GitPullState.IndexNotVerifiable, "The repository configuration or filesystem does not support safe worktree inspection.");
        pullSettings ??= diagnosticSession!.Settings;

        //CSJ,variable=index,インデックス
        var index = await RunDirectAsync(["ls-files", "--stage", "-v", "-z"], token, pullSettings: pullSettings).ConfigureAwait(false);
        if (!index.Success)
            return new(GitPullState.Unavailable, "Could not inspect the Git index.");
        // Lowercase marks assume-unchanged entries. S marks skip-worktree, including sparse checkout.
        //CSJ,variable=indexEntries,インデックス項目一覧
        var indexEntries = index.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (indexEntries.Any(entry => entry[0] is 'S' or (>= 'a' and <= 'z')))
            return new(GitPullState.IndexNotVerifiable, "The Git index can conceal changes in the worktree.");
        if (indexEntries.Any(entry => entry.Length >= 2 && entry.AsSpan(2).StartsWith("160000 ", StringComparison.Ordinal)))
            return new(GitPullState.IndexNotVerifiable, "Submodule worktrees need a separate assessment.");

        // Configured clean/process filters can run while Git compares worktree contents.
        // Leave such worktrees unverified rather than requesting their status.
        var filters = await RunDirectAsync(
            ["config", "--name-only", "--get-regexp", @"^filter\."], token, pullSettings: pullSettings).ConfigureAwait(false);
        if (filters.Success)
            return new(GitPullState.IndexNotVerifiable, "Configured Git filters prevent a read-only worktree assessment.");
        if (filters.ExitCode != 1 || filters.StandardOutput.Length != 0 || filters.StandardError.Length != 0)
            return new(GitPullState.Unavailable, "Could not verify Git filter configuration.");

        //CSJ,variable=status,状態
        var status = await RunDirectAsync(
            ["--no-optional-locks", "status", "--porcelain=v1", "-z", "--untracked-files=normal", "--ignore-submodules=none"], token, pullSettings: pullSettings).ConfigureAwait(false);
        return status.Success
            ? new(Index: index.StandardOutput, Status: status.StandardOutput)
            : new(GitPullState.Unavailable, "Could not inspect the worktree and index.");
    }

    //CSJ,method=HasGitEntry,Git項目の有無を判定する
    private static bool HasGitEntry(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private async Task<GitPullState> ClassifyPullAsync(string local, string upstream, CancellationToken token, string[]? pullSettings = null)
    {
        if (local == upstream) return GitPullState.UpToDate;
        var forward = await RunDirectAsync(["-c", "advice.graftFileDeprecated=false", "merge-base", "--is-ancestor", local, upstream], token, ignoreLegacyGrafts: true, pullSettings: pullSettings).ConfigureAwait(false);
        if (forward.StandardError.Length != 0) return GitPullState.Unavailable;
        if (forward.Success) return GitPullState.FastForwardCandidate;
        if (forward.ExitCode != 1) return GitPullState.Unavailable;
        var backward = await RunDirectAsync(["-c", "advice.graftFileDeprecated=false", "merge-base", "--is-ancestor", upstream, local], token, ignoreLegacyGrafts: true, pullSettings: pullSettings).ConfigureAwait(false);
        if (backward.StandardError.Length != 0) return GitPullState.Unavailable;
        if (backward.Success) return GitPullState.LocalAhead;
        if (backward.ExitCode != 1) return GitPullState.Unavailable;
        var shallow = await RunDirectAsync(["rev-parse", "--is-shallow-repository"], token, pullSettings: pullSettings).ConfigureAwait(false);
        return shallow.Success && shallow.StandardOutput.Trim() == "false" ? GitPullState.Diverged : GitPullState.Unavailable;
    }

    //CSJ,method=PullAsync,プルする（非同期）
    public Task<GitResult> PullAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(UnsafeRemote("Pull requires an explicitly approved repository, branch and target commit."));
    }

    //CSJ,method=PushAsync,プッシュする（非同期）
    public Task<GitResult> PushAsync(CancellationToken token = default) =>
        WithinRemoteDeadlineAsync("Push", deadline => WithAppWriteLeaseAsync(() => PushCoreAsync(null, deadline), deadline), token, RemoteTimeout);

    //CSJ,method=PushTrustedAsync,信頼済みをプッシュする（非同期）
    public Task<GitResult> PushTrustedAsync(string expectedRemote, CancellationToken token = default)
    {
        ValidateRemote(expectedRemote);
        return WithinRemoteDeadlineAsync("Push", deadline => WithAppWriteLeaseAsync(() => PushCoreAsync(expectedRemote, deadline), deadline), token, RemoteTimeout);
    }

    //CSJ,method=PushTrustedAsync,信頼済みをプッシュする（非同期）
    public Task<GitResult> PushTrustedAsync(GitPushTarget target, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return WithinRemoteDeadlineAsync("Push", deadline => WithAppWriteLeaseAsync(() => PushCoreAsync(target.Endpoint, deadline, target), deadline), token, RemoteTimeout);
    }

    //CSJ,method=PushCoreAsync,中核をプッシュする（非同期）
    private async Task<GitResult> PushCoreAsync(string? expectedRemote, CancellationToken token, GitPushTarget? approvedTarget = null,
        //CSJ,parameter=authentication,認証
        GitHubAuthentication authentication = GitHubAuthentication.Anonymous)
    {
        //CSJ,variable=trusted,信頼済み
        var trusted = expectedRemote is not null;
        if (trusted && !HasDirectRepository()) return UnsafeRemote("Select the main repository root on a regular filesystem path.");
        if (approvedTarget is not null && !string.Equals(directory, approvedTarget.RepositoryRoot, StringComparison.OrdinalIgnoreCase))
            return UnsafeRemote("This push approval belongs to a different repository.");
        if (!HasDirectRepository() || !OnFixedLocalNtfs(directory))
            return UnsafeRemote("Push media inspection requires a direct repository on fixed local NTFS.");
        //CSJ,variable=fence,整合性境界
        using var fence = OwnedFilePublication.DirectoryFence.Open(Path.Combine(directory, ".git"), enforceShare: true);
        //CSJ,variable=lease,リース
        using var lease = GitRepositoryLease.TryOpen(directory);
        if (lease is null || (approvedTarget is not null && lease.Identity != approvedTarget.Identity))
            return UnsafeRemote("The approved repository or its Git directory changed. Review the push target again.");
        var (url, error) = await ResolveOriginAsync(push: true, token, literal: trusted).ConfigureAwait(false);
        if (error is not null) return error;
        if (!CanUseRemote(url!, expectedRemote)) return UnsafeRemote("The origin differs from the approved endpoint or requires explicit trust.");
        var (current, tipError) = await ReadPushTargetAsync(url!, trusted: true, token, lease.Identity).ConfigureAwait(false);
        if (tipError is not null) return tipError;
        if (approvedTarget is not null && approvedTarget != current)
            return UnsafeRemote("The branch or commit changed since approval. Review the push target again.");
        BeforeRemoteExecution?.Invoke(token);
        if (trusted && !HasDirectRepository()) return UnsafeRemote("Repository path changed before push.");
        if (approvedTarget is not null)
        {
            var (lastTarget, _) = await ReadPushTargetAsync(url!, trusted: true, token, lease.Identity).ConfigureAwait(false);
            if (approvedTarget != lastTarget)
                return UnsafeRemote("The branch or commit changed before push. Review the push target again.");
        }

        // Pass the same approved object and full destination branch even if HEAD changes
        // after the final observation. Never ask Git to resolve a mutable source ref.
        var target = approvedTarget ?? current!;
        using var config = ProjectListReplacement.Open(Path.Combine(directory, ".git", "config"));
        using var registry = OpenAssetRegistryLease();
        if (!await VerifyLiteralTransportEndpointAsync(target.Endpoint, token).ConfigureAwait(false))
            return UnsafeRemote("The Push endpoint can also resolve as a configured remote name.");
        if (!HasOrdinaryPublicationObjectStore())
            return PublicationRefusal("This Git object store cannot be inspected safely.");
        //CSJ,variable=publicationError,確定反映エラー
        var publicationError = await CheckPushPublicationAsync(target.ObjectId, token).ConfigureAwait(false);
        if (publicationError is not null) return publicationError;
        if (!HasOrdinaryPublicationObjectStore())
            return PublicationRefusal("The Git object store changed during inspection.");
        return await RunRemoteAsync(target.Endpoint,
            ["-c", "push.followTags=false", "-c", "push.negotiate=false", "-c", "push.pushOption=",
             "push", "--porcelain", "--no-follow-tags", "--no-verify", "--signed=no", "--recurse-submodules=no",
             "--", target.Endpoint, target.ObjectId + ":" + target.BranchRef], token, trusted, classifyPushStatus: true,
            authentication: authentication).ConfigureAwait(false);
    }

    //CSJ,method=ReadPushTargetAsync,プッシュ対象を読み取る（非同期）
    private async Task<(GitPushTarget? Target, GitResult? Error)> ReadPushTargetAsync(string url, bool trusted, CancellationToken token,
        //CSJ,parameter=identity,識別情報
        GitRepositoryIdentity? identity)
    {
        var (branch, error) = await ReadBranchTipAsync(trusted, token).ConfigureAwait(false);
        return error is not null ? (null, error) : (new GitPushTarget(directory, url, branch!.BranchRef, branch.ObjectId, identity), null);
    }

    //CSJ,record=BranchTip,ブランチ先端
    private sealed record BranchTip(string BranchRef, string ObjectId);

    private async Task<(BranchTip? Branch, GitResult? Error)> ReadBranchTipAsync(bool trusted, CancellationToken token, string[]? pullSettings = null)
    {
        //CSJ,variable=branch,ブランチ
        var branch = await ReadGitAsync(["symbolic-ref", "--quiet", "HEAD"]).ConfigureAwait(false);
        if (!branch.Success) return (null, new GitResult(branch.ExitCode, "", "A branch must be checked out."));
        //CSJ,variable=name,名前
        var name = branch.StandardOutput.TrimEnd('\r', '\n');
        if (!name.StartsWith(BranchRefPrefix, StringComparison.Ordinal) || name.Length == BranchRefPrefix.Length || HasHiddenText(name))
            return (null, new GitResult(1, "", "Invalid branch name."));
        //CSJ,variable=commit,コミット
        var commit = await ReadGitAsync(["rev-parse", "--verify", name + "^{commit}"]).ConfigureAwait(false);
        //CSJ,variable=objectId,オブジェクトID
        var objectId = commit.StandardOutput.Trim();
        if (!commit.Success || !IsObjectId(objectId))
            return (null, new GitResult(1, "", "Could not determine the branch commit."));
        return (new BranchTip(name, objectId), null);

        //CSJ,method=ReadGitAsync,Gitを読み取る（非同期）
        Task<GitResult> ReadGitAsync(string[] args) => trusted ? RunDirectAsync(args, token, pullSettings: pullSettings) : RunAsync(args, token);
    }

    private Task<GitResult> RunDirectAsync(string[] args, CancellationToken token, bool ignoreLegacyGrafts = false,
        //CSJ,parameter=pullSettings,プル設定
        string[]? pullSettings = null, Action<Process>? afterStart = null, string? standardInput = null, int? outputLimit = null,
        string? privateIndex = null, Func<Process, CancellationToken, Task<(string Text, bool Truncated)>>? interaction = null) =>
        RunAsync([$"--git-dir={Path.Combine(directory, ".git")}", $"--work-tree={directory}", .. args], token,
            trustedProcess: true, ignoreLegacyGrafts: ignoreLegacyGrafts, pullSettings: pullSettings, afterStart: afterStart,
            standardInput: standardInput, outputLimit: outputLimit, privateIndex: privateIndex, interaction: interaction);

    //CSJ,method=IsObjectId,オブジェクトIDかを判定する
    private static bool IsObjectId(string value) => (value.Length == 40 || value.Length == 64) && value.All(Uri.IsHexDigit);

    //CSJ,method=RunRemoteAsync,リモートを実行する（非同期）
    private async Task<GitResult> RunRemoteAsync(string url, string[] arguments, CancellationToken token,
        //CSJ,parameter=trusted,信頼済み
        bool trusted, bool classifyPushStatus = false, string? cloneStage = null,
        //CSJ,parameter=authentication,認証
        GitHubAuthentication authentication = GitHubAuthentication.Anonymous)
    {
        if (HasGitHubHost(url))
        {
            if (!trusted || !IsSupportedGitHubEndpoint(url)) return UnsupportedGitHubEndpoint();
            //CSJ,variable=session,セッション
            using var session = cloneStage is null ? await OpenPullSessionAsync(token, requireIndex: false).ConfigureAwait(false) : null;
            if (cloneStage is null && session is null) return UnsupportedGitHubRepository();
            if (cloneStage is not null)
                arguments = ["clone", "--template=", "--no-recurse-submodules", "--", url, cloneStage];
            var result = await RunGitHubTransportAsync(url, arguments, token, authentication, session?.Settings ?? [],
                cloneStage).ConfigureAwait(false);
            return SanitizeRemote(result, classifyPushStatus, cloneStage);
        }
        if (authentication != GitHubAuthentication.Anonymous) return UnsupportedGitHubEndpoint();

        if (classifyPushStatus)
        {
            //CSJ,variable=session,セッション
            using var session = await OpenPullSessionAsync(token, requireIndex: false).ConfigureAwait(false);
            if (session is null) return UnsafeRemote("Push requires an ordinary repository without external transport settings.");
            //CSJ,variable=local,ローカル
            var local = Path.IsPathFullyQualified(url);
            using var receiver = local ? OwnedFilePublication.DirectoryFence.Open(url, enforceShare: true) : null;
            //CSJ,variable=transport,通信
            var transport = local ? arguments :
                ["-c", "http.followRedirects=false", "-c", "http.sslVerify=true", "-c", "http.proxy=", .. arguments];
            var result = await RunAsync([$"--git-dir={Path.Combine(directory, ".git")}", $"--work-tree={directory}", .. transport],
                token, allowedProtocol: local ? "file" : "https", trustedProcess: true, pullSettings: session.Settings,
                noLazyFetch: true, preserveReceiverHooks: local).ConfigureAwait(false);
            return SanitizeRemote(result, classifyPushStatus: true, cloneStage: null);
        }

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
        return SanitizeRemote(await RunAsync(arguments, token, protocol, ignoreLegacyGrafts: classifyPushStatus,
            noLazyFetch: classifyPushStatus).ConfigureAwait(false), classifyPushStatus, cloneStage);
    }

    //CSJ,method=InspectCloneRemote,クローンリモートを点検する
    public static GitResult InspectCloneRemote(string remote)
    {
        try { ValidateRemote(remote); return new GitResult(0, remote, ""); }
        catch (ArgumentException) { return UnsafeRemote("Clone source is unsupported or contains credentials."); }
    }

    //CSJ,method=CloneAsync,クローンする（非同期）
    public static Task<GitResult> CloneAsync(string remote, string destination, CancellationToken token = default) =>
        CloneCoreAsync(remote, destination, null, token);

    //CSJ,method=CloneTrustedAsync,信頼済みをクローンする（非同期）
    public static Task<GitResult> CloneTrustedAsync(string remote, string destination, CancellationToken token = default) =>
        WithinRemoteDeadlineAsync("Clone", deadline => CloneCoreAsync(remote, destination, null, deadline, trusted: true), token, Timeout);

    //CSJ,method=CloneCoreAsync,中核をクローンする（非同期）
    internal static Task<GitResult> CloneCoreAsync(string remote, string destination, Action? beforeMove, CancellationToken token) =>
        WithinRemoteDeadlineAsync("Clone", deadline => CloneCoreAsync(remote, destination, beforeMove, deadline, trusted: false), token, Timeout);

    //CSJ,method=CloneCoreAsync,中核をクローンする（非同期）
    private static async Task<GitResult> CloneCoreAsync(string remote, string destination, Action? beforeMove, CancellationToken token, bool trusted,
        //CSJ,parameter=authentication,認証
        GitHubAuthentication authentication = GitHubAuthentication.Anonymous)
    {
        token.ThrowIfCancellationRequested();
        ValidateRemote(remote);
        if (authentication != GitHubAuthentication.Anonymous && !IsSupportedGitHubEndpoint(remote))
            return UnsupportedGitHubEndpoint();
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        //CSJ,variable=full,絶対パス
        var full = Path.GetFullPath(destination);
        if (Directory.Exists(full) || File.Exists(full)) throw new IOException("Clone destination already exists.");
        var parent = Path.GetDirectoryName(full) ?? throw new ArgumentException("Invalid clone destination.", nameof(destination));
        if (!trusted && !Path.IsPathFullyQualified(remote)) return UnsafeRemote("Cloning an HTTPS origin requires explicit trust.");
        //CSJ,variable=reserved,予約済み
        GitCloneDirectory reserved;
        try { reserved = GitCloneDirectory.Reserve(parent); }
        catch (IOException)
        { return UnsafeRemote("Could not safely reserve a clone directory under the selected parent."); }
        //CSJ,variable=staging,準備領域
        using var staging = reserved;
        //CSJ,variable=repository,リポジトリ
        var repository = new GitRepository(parent);
        GitResult result;
        try
        {
            result = await repository.RunRemoteAsync(remote, ["clone", "--no-recurse-submodules", "--", remote, staging.Path],
                token, trusted, cloneStage: staging.Path, authentication: authentication).ConfigureAwait(false);
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

        //CSJ,method=ReadLocalSettingAsync,ローカル設定を読み取る（非同期）
        Task<GitResult> ReadLocalSettingAsync(string key) =>
            RunAsync([$"--git-dir={Path.Combine(directory, ".git")}", "config", "--local", "--no-includes", "-z", "--get-all", key], token, trustedProcess: true);
    }

    private static GitResult UnsafeRemote(string message) => new(1, "", message, GitFailure.UnsafeOperation);

    private static bool CanUseRemote(string url, string? expectedRemote) => expectedRemote is null
        ? Path.IsPathFullyQualified(url)
        : string.Equals(url, expectedRemote, StringComparison.Ordinal);

    private bool HasDirectRepository()
    {
        //CSJ,variable=gitDirectory,Git ディレクトリ
        var gitDirectory = Path.Combine(directory, ".git");
        return !HasHiddenText(directory) && IsOrdinaryDirectory(gitDirectory) && IsOrdinaryDirectory(Path.Combine(gitDirectory, "objects")) &&
            IsRegularFile(Path.Combine(gitDirectory, "config")) && IsRegularFile(Path.Combine(gitDirectory, "HEAD")) &&
            !Path.Exists(Path.Combine(gitDirectory, "commondir"));
    }

    //CSJ,method=IsOrdinaryDirectory,通常ディレクトリかを判定する
    private static bool IsOrdinaryDirectory(string path)
    {
        //CSJ,variable=directory,ディレクトリ
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            if (!HasAttributes(directory.FullName, FileAttributes.Directory, FileAttributes.ReparsePoint)) return false;
        }
        return true;
    }

    private static bool IsRegularFile(string path) => HasAttributes(path, 0, FileAttributes.Directory | FileAttributes.ReparsePoint);

    //CSJ,method=HasAttributes,属性の有無を判定する
    private static bool HasAttributes(string path, FileAttributes required, FileAttributes forbidden)
    {
        try
        {
            //CSJ,variable=attributes,属性
            var attributes = File.GetAttributes(path);
            return (attributes & required) == required && (attributes & forbidden) == 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static async Task<GitResult> WithinRemoteDeadlineAsync(string operation, Func<CancellationToken, Task<GitResult>> run,
        CancellationToken token, TimeSpan timeout)
    {
        //CSJ,variable=clock,時計
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

    //CSJ,method=ValidateRemote,リモートを検証する
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
    internal static GitResult SanitizeRemote(GitResult result, bool classifyPushStatus, string? cloneStage)
    {
        if (result.Failure != GitFailure.None) return result;
        if (result.Success) return new GitResult(result.ExitCode, "", "");
        //CSJ,variable=category,分類
        var category = classifyPushStatus && HasNonFastForwardRejection(result.StandardOutput)
            ? GitFailure.Divergence : ClassifyRemoteError(result.StandardError, cloneStage);
        //CSJ,variable=message,メッセージ
        var message = category switch
        {
            GitFailure.PathTooLong => "Git could not represent a filesystem path. Use a shorter repository or clone destination path.",
            GitFailure.Authentication => "GitHub or the remote could not authenticate this request. Check the selected credential mode and account access.",
            GitFailure.Permission => "The current account or remote denied permission for this operation.",
            GitFailure.RemoteRedirect => "The HTTPS endpoint requested a redirect. Confirm the new canonical repository URL before retrying.",
            _ => "Remote operation failed. Check authentication, connectivity and branch state.",
        };
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
        if (Contains("unable to update url base from redirection") ||
            new[] { 301, 302, 303, 307, 308 }.Any(status => Contains($"requested URL returned error: {status}")))
            return GitFailure.RemoteRedirect;
        if (Contains("permission denied") || Contains("requested URL returned error: 403")) return GitFailure.Permission;
        if (Contains("authentication failed") || Contains("could not read Username") || Contains("requested URL returned error: 401") ||
            Contains("interactive authentication has been disabled") || Contains("cannot prompt because user interactivity has been disabled"))
            return GitFailure.Authentication;
        if (Contains("could not resolve host") || Contains("couldn't connect")) return GitFailure.Network;
        return GitFailure.Unknown;
    }

    private static bool HasNonFastForwardRejection(string porcelain)
    {
        foreach (var line in porcelain.Split('\n'))
        {
            if (!line.StartsWith("!\t", StringComparison.Ordinal)) continue;
            //CSJ,variable=separator,区切り
            var separator = line.LastIndexOf('\t');
            //CSJ,variable=status,状態
            var status = line[(separator + 1)..].TrimEnd('\r');
            if (status is "[rejected] (non-fast-forward)" or "[rejected] (fetch first)") return true;
        }
        return false;
    }

    //CSJ,method=RunBranchAsync,ブランチを実行する（非同期）
    private Task<GitResult> RunBranchAsync(string[] command, string name, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.StartsWith('-') || name.Contains('\0')) throw new ArgumentException("Invalid branch name.", nameof(name));
        return RunAsync([.. command, name], token);
    }
}
