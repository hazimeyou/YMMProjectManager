using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace YMMProjectManager.Infrastructure.Git;

//CSJ,record=GitDirectoryIdentity,Git ディレクトリ識別情報
internal readonly record struct GitDirectoryIdentity(ulong Volume, Guid FileId);
//CSJ,record=GitRepositoryIdentity,Gitリポジトリ識別情報
internal sealed record GitRepositoryIdentity(GitDirectoryIdentity Worktree, GitDirectoryIdentity GitDirectory);

/// <summary>Keeps the observed worktree and Git directory open without permitting their deletion.</summary>
//CSJ,class=GitRepositoryLease,Gitリポジトリ参照保持
internal sealed class GitRepositoryLease(SafeFileHandle worktree, SafeFileHandle gitDirectory, GitRepositoryIdentity identity) : IDisposable
{
    //CSJ,field=ReadAttributes,読み取り属性
    private const uint ReadAttributes = 0x80;
    //CSJ,field=OpenExisting,表示既存
    private const uint OpenExisting = 3;
    //CSJ,field=OpenReparsePoint,表示再解析ポイント
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    //CSJ,field=FileAttributeTagInfo,ファイル属性タグ情報
    private const int FileAttributeTagInfo = 9;
    //CSJ,field=FileIdInfo,ファイルID情報
    private const int FileIdInfo = 18;

    //CSJ,property=Identity,識別情報
    internal GitRepositoryIdentity Identity { get; } = identity;

    //CSJ,method=TryOpen,表示を試みる
    internal static GitRepositoryLease? TryOpen(string repositoryRoot)
    {
        //CSJ,variable=worktree,作業ツリー
        SafeFileHandle? worktree = null;
        //CSJ,variable=gitDirectory,Git ディレクトリ
        SafeFileHandle? gitDirectory = null;
        try
        {
            worktree = OpenDirectory(repositoryRoot);
            gitDirectory = OpenDirectory(Path.Combine(repositoryRoot, ".git"));
            //CSJ,variable=worktreeId,作業ツリーID
            var worktreeId = ReadIdentity(worktree);
            //CSJ,variable=gitDirectoryId,Git ディレクトリID
            var gitDirectoryId = ReadIdentity(gitDirectory);
            if (worktreeId is null || gitDirectoryId is null) return null;
            //CSJ,variable=lease,リース
            var lease = new GitRepositoryLease(worktree, gitDirectory, new(worktreeId.Value, gitDirectoryId.Value));
            worktree = null;
            gitDirectory = null;
            return lease;
        }
        finally
        {
            gitDirectory?.Dispose();
            worktree?.Dispose();
        }
    }

    //CSJ,method=Dispose,破棄する
    public void Dispose()
    {
        gitDirectory.Dispose();
        worktree.Dispose();
    }

    //CSJ,method=OpenDirectory,ディレクトリを開く
    internal static SafeFileHandle OpenDirectory(string path, FileShare share = FileShare.ReadWrite, bool allowRename = false)
    {
        //CSJ,variable=delete,削除
        const uint delete = 0x10000;
        //CSJ,variable=access,アクセス
        var access = ReadAttributes | (allowRename ? delete : 0);
        return CreateFile(ExtendedPath(path), access, share, IntPtr.Zero,
            OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
    }

    internal static string ExtendedPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path :
        path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    //CSJ,method=ReadIdentity,識別情報を読み取る
    internal static GitDirectoryIdentity? ReadIdentity(SafeFileHandle handle)
    {
        if (handle.IsInvalid || !ReadAttributesByHandle(handle, FileAttributeTagInfo, out var attributes, 8) ||
            (attributes.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory ||
            !ReadIdentityByHandle(handle, FileIdInfo, out var identity, (uint)Marshal.SizeOf<NativeFileId>()) ||
            identity.Id == Guid.Empty)
            return null;
        return new GitDirectoryIdentity(identity.Volume, identity.Id);
    }

    //CSJ,struct=NativeFileId,ネイティブファイルID
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileId
    {
        //CSJ,field=Volume,ボリューム
        internal ulong Volume;
        //CSJ,field=Id,識別ID
        internal Guid Id;
    }

    //CSJ,struct=NativeAttributes,ネイティブ属性
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeAttributes
    {
        //CSJ,field=Attributes,属性
        internal FileAttributes Attributes;
        //CSJ,field=ReparseTag,再解析タグ
        internal uint ReparseTag;
    }

    //CSJ,method=CreateFile,ファイルを作成する
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr securityAttributes,
        //CSJ,parameter=mode,モード
        uint mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadAttributesByHandle(SafeFileHandle handle, int informationClass, out NativeAttributes info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadIdentityByHandle(SafeFileHandle handle, int informationClass, out NativeFileId info, uint size);
}
