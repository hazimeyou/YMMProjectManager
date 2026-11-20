using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace YMMProjectManager.Infrastructure.Git;

internal sealed class GitCloneDirectory : IDisposable
{
    private const uint Synchronize = 0x00100000;
    private const uint ReadAttributes = 0x80;
    private const uint NormalAttributes = 0x80;
    private const uint FileCreate = 2;
    private const uint DirectoryFile = 1;
    private const uint SynchronousIoNonAlert = 0x20;
    private const uint CaseInsensitive = 0x40;
    private const uint DontReparse = 0x1000;
    private const int FileRenameInfo = 3;
    private readonly List<SafeFileHandle> ancestors;
    private readonly SafeFileHandle stagePin;
    private readonly GitDirectoryIdentity identity;
    private SafeFileHandle? identityHandle;

    private GitCloneDirectory(string path, List<SafeFileHandle> ancestors, SafeFileHandle stagePin, GitDirectoryIdentity identity)
    {
        Path = path;
        this.ancestors = ancestors;
        this.stagePin = stagePin;
        this.identity = identity;
    }

    internal string Path { get; }

    internal static GitCloneDirectory Reserve(string parent)
    {
        var paths = new Stack<string>();
        for (var cursor = new DirectoryInfo(parent); cursor is not null; cursor = cursor.Parent)
            paths.Push(cursor.FullName);
        var ancestors = new List<SafeFileHandle>();
        SafeFileHandle? stage = null;
        try
        {
            while (paths.TryPop(out var path))
            {
                var ancestor = GitRepositoryLease.OpenDirectory(path, FileShare.Read);
                ancestors.Add(ancestor);
                if (GitRepositoryLease.ReadIdentity(ancestor) is null)
                    throw new IOException("Clone destination path cannot be protected.");
            }

            var name = ".ymmpm-clone-" + Guid.NewGuid().ToString("N");
            stage = CreateAndPin(ancestors[^1], name);
            var owned = GitRepositoryLease.ReadIdentity(stage) ?? throw new IOException("Could not identify the new clone staging directory.");
            return new GitCloneDirectory(System.IO.Path.Combine(parent, name), ancestors, stage, owned);
        }
        catch
        {
            stage?.Dispose();
            DisposeAncestors(ancestors);
            throw;
        }
    }

    internal void Publish(string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Git's checkout cannot use a root held open with DELETE access. Open an
        // identity witness while the original root is still pinned, then acquire a
        // rename handle and check its physical identity before the publication.
        identityHandle = GitRepositoryLease.OpenDirectory(Path, FileShare.ReadWrite | FileShare.Delete);
        if (GitRepositoryLease.ReadIdentity(identityHandle) != identity)
            throw new IOException("Clone staging identity changed.");
        stagePin.Dispose();
        using var publication = GitRepositoryLease.OpenDirectory(Path, allowRename: true);
        if (GitRepositoryLease.ReadIdentity(publication) != identity)
            throw new IOException("Clone staging could not be acquired for publication.");
        cancellationToken.ThrowIfCancellationRequested();
        RenameWithoutReplacement(publication, destination);
    }

    internal string? CheckRecoveryPath()
    {
        using var current = GitRepositoryLease.OpenDirectory(Path, FileShare.ReadWrite | FileShare.Delete);
        return GitRepositoryLease.ReadIdentity(current) == identity ? Path : null;
    }

    public void Dispose()
    {
        identityHandle?.Dispose();
        stagePin.Dispose();
        DisposeAncestors(ancestors);
    }

    private static void DisposeAncestors(List<SafeFileHandle> handles)
    {
        for (var index = handles.Count - 1; index >= 0; index--) handles[index].Dispose();
    }

    private static SafeFileHandle CreateAndPin(SafeFileHandle parent, string name)
    {
        var text = Marshal.StringToHGlobalUni(name);
        var namePointer = IntPtr.Zero;
        try
        {
            namePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(new UnicodeString
            {
                Length = checked((ushort)(name.Length * sizeof(char))),
                MaximumLength = checked((ushort)((name.Length + 1) * sizeof(char))),
                Buffer = text,
            }, namePointer, false);
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = namePointer,
                Attributes = CaseInsensitive | DontReparse,
            };
            // FILE_CREATE creates and returns a handle to the same directory in a
            // single call. A collision or reparse must fail before Git is started.
            var status = NtCreateFile(out var handle, ReadAttributes | Synchronize, ref attributes, out _, IntPtr.Zero,
                NormalAttributes, FileShare.ReadWrite, FileCreate, DirectoryFile | SynchronousIoNonAlert, IntPtr.Zero, 0);
            GC.KeepAlive(parent);
            if (status == 0 && handle is { IsInvalid: false }) return handle;
            handle?.Dispose();
            throw new IOException("Could not reserve a clone staging directory.", new Win32Exception((int)RtlNtStatusToDosError(status)));
        }
        finally
        {
            Marshal.FreeHGlobal(namePointer);
            Marshal.FreeHGlobal(text);
        }
    }

    private static void RenameWithoutReplacement(SafeFileHandle handle, string destination)
    {
        // FILE_RENAME_INFO contains a pointer-sized RootDirectory followed by a
        // byte count and UTF-16 name. RootDirectory and ReplaceIfExists stay zero.
        var name = Encoding.Unicode.GetBytes(GitRepositoryLease.ExtendedPath(destination));
        var lengthOffset = 2 * IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(int);
        var data = new byte[nameOffset + name.Length + sizeof(char)];
        BitConverter.TryWriteBytes(data.AsSpan(lengthOffset), name.Length);
        name.CopyTo(data, nameOffset);
        if (!SetFileInformationByHandle(handle, FileRenameInfo, data, (uint)data.Length))
            throw new IOException("The approved clone could not be published without replacement.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal uint Length;
        internal IntPtr RootDirectory;
        internal IntPtr ObjectName;
        internal uint Attributes;
        internal IntPtr SecurityDescriptor;
        internal IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal IntPtr Status;
        internal UIntPtr Information;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatusBlock status, IntPtr allocationSize, uint fileAttributes, FileShare share, uint disposition, uint options,
        IntPtr extendedAttributes, uint extendedAttributesLength);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, byte[] information, uint size);
}
