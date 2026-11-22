using System.IO;
using System.Text.Json;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Infrastructure.Git;

/// <summary>Blocks a second Pull when the previous mutation has not been verified.</summary>
//CSJ,class=GitPullOperationJournal,Gitプル操作作業記録
internal sealed class GitPullOperationJournal : IDisposable
{
    //CSJ,field=JournalName,作業記録名前
    private const string JournalName = "ymmpm-pull-in-progress.json";
    //CSJ,record=ApprovalEvidence,承認証跡
    internal sealed record ApprovalEvidence(string BranchRef, string StartObjectId, string RemoteName,
        string UpstreamRef, string TargetObjectId);
    //CSJ,field=stream,ストリーム
    private readonly FileStream stream;
    //CSJ,field=directoryFence,ディレクトリ整合性境界
    private readonly OwnedFilePublication.DirectoryFence directoryFence;

    private GitPullOperationJournal(string path, FileStream stream, OwnedFilePublication.DirectoryFence directoryFence)
    {
        Path = path;
        this.stream = stream;
        this.directoryFence = directoryFence;
    }

    //CSJ,property=Path,パス
    internal string Path { get; }
    //CSJ,method=GetPath,パスを取得する
    internal static string GetPath(string repositoryRoot) => System.IO.Path.Combine(repositoryRoot, ".git", JournalName);

    //CSJ,method=Exists,存在するか判定する
    internal static bool Exists(string repositoryRoot)
    {
        try
        {
            _ = File.GetAttributes(GetPath(repositoryRoot));
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        // An unreadable record must also block a new operation.
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    //CSJ,method=Create,作成する
    internal static GitPullOperationJournal Create(string repositoryRoot, ApprovalEvidence target)
    {
        //CSJ,variable=path,パス
        var path = GetPath(repositoryRoot);
        var parent = System.IO.Path.GetDirectoryName(path)!;
        // Use no-overwrite publication from the held file. A competing Pull or unknown file
        // at the final name is a reason to stop before starting any working-tree mutation.
        //CSJ,variable=fence,整合性境界
        var fence = OwnedFilePublication.DirectoryFence.Open(parent, enforceShare: true);
        FileStream? output = null;
        try
        {
            var id = Guid.NewGuid();
            //CSJ,variable=evidence,証跡
            var evidence = new
            {
                Version = 1,
                OperationId = id,
                State = "MutationPrepared",
                CreatedUtc = DateTimeOffset.UtcNow,
                target.BranchRef,
                target.StartObjectId,
                target.RemoteName,
                target.UpstreamRef,
                target.TargetObjectId,
            };
            output = OwnedFilePublication.CreateOwnedOutputFile(System.IO.Path.Combine(parent, $".ymmpm-pull-{id:N}.tmp"));
            JsonSerializer.Serialize(output, evidence);
            output.Flush(flushToDisk: true);
            OwnedFilePublication.RenameOpenedPath(output.SafeFileHandle, path);
            return new GitPullOperationJournal(path, output, fence);
        }
        catch
        {
            if (output is not null)
            {
                OwnedFilePublication.TryDeleteOpenedPath(output.SafeFileHandle);
                output.Dispose();
            }
            fence.Dispose();
            throw;
        }
    }

    /// <summary>Clears the exact record after the caller verifies a terminal outcome.</summary>
    //CSJ,method=Clear,消去する
    internal bool Clear() => OwnedFilePublication.TryDeleteOpenedPath(stream.SafeFileHandle);

    // Releasing handles preserves pending evidence on unknown outcomes.
    //CSJ,method=Dispose,破棄する
    public void Dispose()
    {
        try { stream.Dispose(); }
        finally { directoryFence.Dispose(); }
    }
}
