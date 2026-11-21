using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace YMMProjectManager.Infrastructure.Git;

internal readonly record struct GitDirectoryIdentity(ulong Volume, Guid FileId);
internal sealed record GitRepositoryIdentity(GitDirectoryIdentity Worktree, GitDirectoryIdentity GitDirectory);

/// <summary>Keeps the observed worktree and Git directory open without permitting their deletion.</summary>
internal sealed class GitRepositoryLease(SafeFileHandle worktree, SafeFileHandle gitDirectory, GitRepositoryIdentity identity) : IDisposable
{
    private const uint ReadAttributes = 0x80;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const int FileAttributeTagInfo = 9;
    private const int FileIdInfo = 18;

    internal GitRepositoryIdentity Identity { get; } = identity;

    internal static GitRepositoryLease? TryOpen(string repositoryRoot)
    {
        SafeFileHandle? worktree = null;
        SafeFileHandle? gitDirectory = null;
        try
        {
            worktree = OpenDirectory(repositoryRoot);
            gitDirectory = OpenDirectory(Path.Combine(repositoryRoot, ".git"));
            var worktreeId = ReadIdentity(worktree);
            var gitDirectoryId = ReadIdentity(gitDirectory);
            if (worktreeId is null || gitDirectoryId is null) return null;
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

    public void Dispose()
    {
        gitDirectory.Dispose();
        worktree.Dispose();
    }

    internal static SafeFileHandle OpenDirectory(string path, FileShare share = FileShare.ReadWrite, bool allowRename = false)
    {
        const uint delete = 0x10000;
        var access = ReadAttributes | (allowRename ? delete : 0);
        return CreateFile(ExtendedPath(path), access, share, IntPtr.Zero,
            OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
    }

    internal static string ExtendedPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path :
        path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    internal static GitDirectoryIdentity? ReadIdentity(SafeFileHandle handle)
    {
        if (handle.IsInvalid || !ReadAttributesByHandle(handle, FileAttributeTagInfo, out var attributes, 8) ||
            (attributes.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory ||
            !ReadIdentityByHandle(handle, FileIdInfo, out var identity, (uint)Marshal.SizeOf<NativeFileId>()) ||
            identity.Id == Guid.Empty)
            return null;
        return new GitDirectoryIdentity(identity.Volume, identity.Id);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileId
    {
        internal ulong Volume;
        internal Guid Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeAttributes
    {
        internal FileAttributes Attributes;
        internal uint ReparseTag;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr securityAttributes,
        uint mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadAttributesByHandle(SafeFileHandle handle, int informationClass, out NativeAttributes info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadIdentityByHandle(SafeFileHandle handle, int informationClass, out NativeFileId info, uint size);
}
