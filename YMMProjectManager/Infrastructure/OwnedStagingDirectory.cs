using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace YMMProjectManager.Infrastructure;

internal sealed class OwnedStagingDirectory : IDisposable
{
    private const uint Synchronize = 0x00100000;
    private const uint ReadAttributes = 0x80;
    private const uint NormalAttributes = 0x80;
    private const uint FileCreate = 2;
    private const uint DirectoryFile = 1;
    private const uint SynchronousIoNonAlert = 0x20;
    private const uint CaseInsensitive = 0x40;
    private const uint DontReparse = 0x1000;
    private readonly List<SafeFileHandle> ancestors;
    private readonly SafeFileHandle stagePin;
    private readonly (ulong Volume, Guid FileId) identity;
    private SafeFileHandle? identityHandle;

    private OwnedStagingDirectory(string path, List<SafeFileHandle> ancestors, SafeFileHandle stagePin, (ulong Volume, Guid FileId) identity)
    {
        Path = path;
        this.ancestors = ancestors;
        this.stagePin = stagePin;
        this.identity = identity;
    }

    internal string Path { get; }

    internal static OwnedStagingDirectory Reserve(string parent, string prefix)
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
                var ancestor = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: true, allowRename: false);
                ancestors.Add(ancestor);
            }

            var name = prefix + Guid.NewGuid().ToString("N");
            stage = CreateAndPin(ancestors[^1], name);
            var owned = ProjectFileEvidence.ReadPhysicalIdentity(stage);
            return new OwnedStagingDirectory(System.IO.Path.Combine(parent, name), ancestors, stage, owned);
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
        using var publication = OpenForPublication();
        cancellationToken.ThrowIfCancellationRequested();
        OwnedFilePublication.RenameOpenedPath(publication, destination);
    }

    internal bool DeleteIfEmpty()
    {
        using var publication = OpenForPublication();
        return OwnedFilePublication.TryDeleteOpenedPath(publication);
    }

    internal string? CheckRecoveryPath()
    {
        try
        {
            using var current = OwnedFilePublication.OpenOrdinaryPath(Path, expectDirectory: true, allowRename: false,
                FileShare.ReadWrite | FileShare.Delete);
            return ProjectFileEvidence.ReadPhysicalIdentity(current) == identity ? Path : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private SafeFileHandle OpenForPublication()
    {
        // Producers can use the staging directory while its original handle denies rename. Acquire a
        // witness before releasing that pin, then verify the exact identity on the rename handle.
        identityHandle ??= OwnedFilePublication.OpenOrdinaryPath(Path, expectDirectory: true, allowRename: false,
            FileShare.ReadWrite | FileShare.Delete);
        if (ProjectFileEvidence.ReadPhysicalIdentity(identityHandle) != identity)
            throw new IOException("Staging directory identity changed.");
        stagePin.Dispose();
        var publication = OwnedFilePublication.OpenOrdinaryPath(Path, expectDirectory: true, allowRename: true);
        try
        {
            if (ProjectFileEvidence.ReadPhysicalIdentity(publication) != identity)
                throw new IOException("Staging directory could not be acquired for publication.");
            return publication;
        }
        catch
        {
            publication.Dispose();
            throw;
        }
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
            throw new IOException("Could not reserve a staging directory.", new Win32Exception((int)RtlNtStatusToDosError(status)));
        }
        finally
        {
            Marshal.FreeHGlobal(namePointer);
            Marshal.FreeHGlobal(text);
        }
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
}
