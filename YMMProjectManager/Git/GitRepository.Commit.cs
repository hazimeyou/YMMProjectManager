using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace YMMProjectManager.Infrastructure.Git;

//CSJ,class=GitRepository,Gitリポジトリ操作
public sealed partial class GitRepository
{
    //CSJ,property=BeforeCommitRefUpdate,コミット参照更新前の処理
    internal Action? BeforeCommitRefUpdate { get; init; }
    internal Action<Process>? AfterCommitAcknowledged { get; init; }

    //CSJ,method=InspectCommitAsync,コミットを点検する（非同期）
    public Task<GitResult> InspectCommitAsync(CancellationToken token = default) =>
        WithAppWriteLeaseAsync(() => RunCommitCoreAsync(null, null, token), token);

    //CSJ,method=CommitAsync,コミット（非同期）
    public Task<GitResult> CommitAsync(GitCommitTarget target, string message, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return WithAppWriteLeaseAsync(() => RunCommitCoreAsync(target, message, token), token);
    }

    //CSJ,method=RunCommitCoreAsync,コミット中核を実行する（非同期）
    private async Task<GitResult> RunCommitCoreAsync(GitCommitTarget? approved, string? message, CancellationToken token)
    {
        if (message is not null && (message.Length > 32768 || message.Contains('\0')))
            return UnsafeRemote("The commit message contains invalid data or exceeds 32,768 characters.");
        if (HasPendingPullEvidence()) return UnresolvedPull();
        //CSJ,variable=session,セッション
        using var session = await OpenPullSessionAsync(token, requireIndex: false).ConfigureAwait(false);
        if (session is null)
            return UnsafeRemote("Commit requires a supported main repository on a fixed local NTFS volume.");
        if (approved is not null && (approved.RepositoryRoot != directory || approved.Identity != session.Identity))
            return UnsafeRemote("The approved repository changed. Review the staged changes again.");

        //CSJ,variable=gitDirectory,Git ディレクトリ
        var gitDirectory = Path.Combine(directory, ".git");
        try
        {
            if (PullOperationMarkers.Any(marker => HasGitEntry(Path.Combine(gitDirectory, marker))))
                return BusyIndex();
            // Other Git commands must acquire index.lock to change staged state or check
            // out another branch. Keep HEAD itself open to also stop symbolic-ref changes.
            //CSJ,variable=indexLock,インデックスロック
            using var indexLock = new FileStream(Path.Combine(gitDirectory, "index.lock"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            if (HasPendingPullEvidence()) return UnresolvedPull();
            if (HasConcurrentCommitMarker(gitDirectory)) return BusyIndex();
            //CSJ,variable=headFile,HEADファイル
            using var headFile = ProjectListReplacement.Open(Path.Combine(gitDirectory, "HEAD"));
            //CSJ,variable=indexFile,インデックスファイル
            using var indexFile = ProjectListReplacement.TryOpen(Path.Combine(gitDirectory, "index"));
            if (indexFile is null) return UnsafeRemote("Stage at least one file before committing.");

            var head = await RunPullCommandAsync(session, ["symbolic-ref", "--quiet", "--no-recurse", "HEAD"], token).ConfigureAwait(false);
            //CSJ,variable=branch,ブランチ
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
            //CSJ,variable=snapshotPath,スナップショットパス
            var snapshotPath = Path.Combine(gitDirectory, "ymm-index-" + Guid.NewGuid().ToString("N"));
            //CSJ,variable=snapshotIdentity,スナップショット識別情報
            (ulong Volume, Guid FileId)? snapshotIdentity = null;
            try
            {
                //CSJ,variable=snapshot,スナップショット
                await using (var snapshot = OwnedFilePublication.CreateOwnedOutputFile(snapshotPath))
                {
                    snapshotIdentity = ProjectFileEvidence.ReadPhysicalIdentity(snapshot.SafeFileHandle);
                    await indexFile.CopyToAsync(snapshot, token).ConfigureAwait(false);
                }
                //CSJ,variable=pinnedSnapshot,保持済みスナップショット
                using var pinnedSnapshot = ProjectListReplacement.Open(snapshotPath);
                if (ProjectFileEvidence.ReadPhysicalIdentity(pinnedSnapshot.SafeFileHandle) != snapshotIdentity)
                    return UnsafeRemote("The private index changed while preparing Commit.");
                // Git can read this copy to write the tree object. Its optional
                // cache-tree refresh cannot replace the held snapshot file.
                //CSJ,variable=treeResult,ツリー結果
                var treeResult = await RunDirectAsync(["write-tree"], token, pullSettings: session.Settings,
                    privateIndex: snapshotPath).ConfigureAwait(false);
                //CSJ,variable=tree,ツリー
                var tree = treeResult.StandardOutput.Trim();
                if (!treeResult.Success || !IsObjectId(tree))
                    return UnsafeRemote("The staged tree is unavailable. Resolve index conflicts before committing.");
                //CSJ,variable=changes,変更一覧
                var changes = parent is null
                    ? new[] { "ls-tree", "-r", "--full-tree", "--name-only", "-z", tree }
                    : new[] { "diff-tree", "--no-commit-id", "--name-only", "-r", "-z", "--no-renames", parent, tree, "--" };
                //CSJ,variable=staged,ステージ済み
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
                //CSJ,variable=mediaError,メディアエラー
                var mediaError = await CheckPublicationTreeAsync(tree, token).ConfigureAwait(false);
                if (mediaError is not null) return mediaError;
                var signing = await ReadGitBooleanSettingAsync("commit.gpgSign", token).ConfigureAwait(false);
                if (signing != false) return UnsafeRemote("Commit signing is required or could not be verified. Use the repository's signing workflow.");
                //CSJ,variable=identitySettings,識別情報設定
                var identitySettings = await ReadCommitIdentityAsync(token).ConfigureAwait(false);
                if (identitySettings is null)
                    return UnsafeRemote("Configure a Git user.name and user.email before committing.");
                var args = new List<string>(identitySettings) { "commit-tree", "--no-gpg-sign", tree };
                if (parent is not null) args.AddRange(["-p", parent]);
                args.AddRange(["-F", "-"]);
                //CSJ,variable=commit,コミット
                var commit = await RunDirectAsync(args.ToArray(), token, pullSettings: session.Settings,
                    standardInput: message + "\n").ConfigureAwait(false);
                //CSJ,variable=objectId,オブジェクトID
                var objectId = commit.StandardOutput.Trim();
                if (!commit.Success || !IsObjectId(objectId))
                    return UnsafeRemote("Git could not create the reviewed commit object. Check Git identity and repository configuration.");

                BeforeCommitRefUpdate?.Invoke();
                token.ThrowIfCancellationRequested();
                if (HasPendingPullEvidence()) return UnresolvedPull();
                if (HasConcurrentCommitMarker(gitDirectory)) return BusyIndex();
                return await PublishCommitRefAsync(session, branch, parent ?? new string('0', tree.Length), objectId).ConfigureAwait(false);
            }
            finally
            {
                if (snapshotIdentity is { } identity) TryCleanCommitIndex(snapshotPath, identity);
            }
        }
        catch (IOException) { return BusyIndex(); }
        catch (UnauthorizedAccessException) { return BusyIndex(); }
        catch (TimeoutException) { return new(1, "", "Commit preparation timed out.", GitFailure.Timeout); }
        catch (Win32Exception) { return UnsafeRemote("Git could not be started for Commit."); }
    }

    //CSJ,method=PublishCommitRefAsync,コミット参照を確定反映する（非同期）
    private async Task<GitResult> PublishCommitRefAsync(PullSession session, string branch, string parent, string commit)
    {
        var commitRequested = false;
        try
        {
            // From here native Git owns the branch CAS. Late UI cancellation cannot
            // change the reported result of a published commit.
            //CSJ,variable=update,更新
            var update = await RunDirectAsync(["update-ref", "-m", "YMMProjectManager commit", "--stdin"], CancellationToken.None,
                pullSettings: session.Settings, interaction: (process, deadline) =>
                    PrepareAndCommitRefAsync(session, process, branch, parent, commit, () => commitRequested = true, deadline))
                .ConfigureAwait(false);
            var observed = await RunPullCommandAsync(session, ["rev-parse", "--verify", "--quiet", branch + "^{commit}"],
                CancellationToken.None).ConfigureAwait(false);
            if (update.Success && update.StandardOutput == "commit: ok" && observed.Success && observed.StandardOutput.Trim() == commit)
                return new GitResult(0, commit, "");
            if (!commitRequested && (!update.Success || update.StandardOutput == "abort: ok"))
                return new(1, "", "Git refused the branch update. Refresh Git state before retrying.", GitFailure.ConcurrentOperation);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or Win32Exception or InvalidOperationException)
        {
            // The command can have updated the ref before its result became unavailable.
        }
        return new(1, "", "The Commit outcome could not be verified. Inspect branch history before retrying.", GitFailure.OutcomeUnknown);
    }

    private async Task<(string Text, bool Truncated)> PrepareAndCommitRefAsync(PullSession session, Process process,
        //CSJ,parameter=branch,ブランチ
        string branch, string parent, string commit, Action markCommitRequested, CancellationToken token)
    {
        try
        {
            await SendAsync($"start\noption no-deref\nupdate {branch} {commit} {parent}\nprepare\n").ConfigureAwait(false);
            if (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) != "start: ok" ||
                await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) != "prepare: ok")
                return ("", false);

            // update-ref can otherwise replace a symbolic branch with the same
            // dereferenced old OID. Check its raw kind while native Git owns the lock.
            var raw = await RunPullCommandAsync(session, ["symbolic-ref", "--quiet", "--no-recurse", branch], token).ConfigureAwait(false);
            //CSJ,variable=ordinary,通常
            var ordinary = raw.ExitCode == 1 && raw.StandardOutput.Length == 0 && raw.StandardError.Length == 0;
            //CSJ,variable=publish,公開
            var publish = ordinary && !HasPendingPullEvidence();
            // A failed write or nonzero exit can follow native publication.
            // Once requested, report an unverified outcome for manual inspection.
            if (publish) markCommitRequested();
            await SendAsync(publish ? "commit\n" : "abort\n").ConfigureAwait(false);
            //CSJ,variable=response,応答
            var response = await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
            if (response == "commit: ok") AfterCommitAcknowledged?.Invoke(process);
            return (response ?? "", false);
        }
        finally
        {
            // EOF also aborts a prepared update that has not received commit.
            process.StandardInput.Close();
        }

        async Task SendAsync(string command)
        {
            await process.StandardInput.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(command), token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(token).ConfigureAwait(false);
        }
    }

    //CSJ,method=ReadCommitIdentityAsync,コミット識別情報を読み取る（非同期）
    private async Task<string[]?> ReadCommitIdentityAsync(CancellationToken token)
    {
        var useConfigOnly = await ReadGitBooleanSettingAsync("user.useConfigOnly", token).ConfigureAwait(false);
        if (useConfigOnly is null) return null;
        //CSJ,variable=settings,設定
        var settings = new List<string>();
        //CSJ,variable=key,キー
        foreach (var key in new[] { "user.name", "user.email" })
        {
            var value = await RunDirectAsync(["config", "-z", "--get", key], token, outputLimit: 2048).ConfigureAwait(false);
            if (!value.Success && value.ExitCode == 1 && value.StandardOutput.Length == 0 && value.StandardError.Length == 0)
            {
                if (useConfigOnly == true) return null;
                continue; // Git may have a usable OS identity.
            }
            if (!value.Success || value.StandardError.Length != 0 || !value.StandardOutput.EndsWith('\0') ||
                HasHiddenText(value.StandardOutput[..^1])) return null;
            settings.Add("-c");
            settings.Add(key + "=" + value.StandardOutput[..^1]);
        }
        return settings.ToArray();
    }

    //CSJ,method=ReadGitBooleanSettingAsync,Git真偽値設定を読み取る（非同期）
    private async Task<bool?> ReadGitBooleanSettingAsync(string key, CancellationToken token)
    {
        var result = await RunDirectAsync(["config", "--type=bool", "--get", key], token, outputLimit: 2048).ConfigureAwait(false);
        if (result.StandardError.Length != 0) return null;
        if (result.ExitCode == 1 && result.StandardOutput.Length == 0) return false;
        return result.Success && bool.TryParse(result.StandardOutput.Trim(), out var enabled) ? enabled : null;
    }

    private static GitResult BusyIndex() =>
        new(1, "", "Git index or repository state is busy. Finish any other Git operation, then review the staged changes.",
            GitFailure.ConcurrentOperation);

    //CSJ,method=HasConcurrentCommitMarker,並行コミットマーカーの有無を判定する
    private static bool HasConcurrentCommitMarker(string gitDirectory) =>
        PullOperationMarkers.Any(marker => marker != "index.lock" && HasGitEntry(Path.Combine(gitDirectory, marker)));

    //CSJ,method=TryCleanCommitIndex,コミットインデックスの変更なしを試みる
    private static void TryCleanCommitIndex(string path, (ulong Volume, Guid FileId) identity)
    {
        try
        {
            using var handle = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename: true);
            if (ProjectFileEvidence.ReadPhysicalIdentity(handle) == identity)
                OwnedFilePublication.TryDeleteOpenedPath(handle);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

//CSJ,class=GitCommitTarget,Gitコミット対象
public sealed class GitCommitTarget
{
    internal GitCommitTarget(string repositoryRoot, string branchRef, string? parentObjectId, string treeObjectId,
        //CSJ,parameter=paths,パス一覧
        IReadOnlyList<string> paths, GitRepositoryIdentity identity)
    {
        RepositoryRoot = repositoryRoot;
        BranchRef = branchRef;
        ParentObjectId = parentObjectId;
        TreeObjectId = treeObjectId;
        Paths = paths;
        Identity = identity;
    }

    //CSJ,property=RepositoryRoot,リポジトリルート
    public string RepositoryRoot { get; }
    //CSJ,property=BranchRef,ブランチ参照
    public string BranchRef { get; }
    //CSJ,property=ParentObjectId,親オブジェクトID
    public string? ParentObjectId { get; }
    //CSJ,property=TreeObjectId,ツリーオブジェクトID
    public string TreeObjectId { get; }
    //CSJ,property=Paths,パス一覧
    public IReadOnlyList<string> Paths { get; }
    //CSJ,property=Identity,識別情報
    internal GitRepositoryIdentity Identity { get; }
}
