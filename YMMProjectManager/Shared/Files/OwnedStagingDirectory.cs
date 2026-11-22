using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace YMMProjectManager.Infrastructure;

//CSJ,class=OwnedStagingDirectory,管理対象の一時作業ディレクトリ
internal sealed class OwnedStagingDirectory : IDisposable
{
    //CSJ,field=Synchronize,同期
    private const uint Synchronize = 0x00100000;
    //CSJ,field=ReadAttributes,読み取り属性
    private const uint ReadAttributes = 0x80;
    private const uint NormalAttributes = 0x80;
    //CSJ,field=FileCreate,ファイル作成
    private const uint FileCreate = 2;
    //CSJ,field=DirectoryFile,ディレクトリファイル
    private const uint DirectoryFile = 1;
    private const uint SynchronousIoNonAlert = 0x20;
    private const uint CaseInsensitive = 0x40;
    private const uint DontReparse = 0x1000;
    private readonly List<SafeFileHandle> ancestors;
    //CSJ,field=stagePin,作業段階固定参照
    private readonly SafeFileHandle stagePin;
    //CSJ,field=identity,識別情報
    private readonly (ulong Volume, Guid FileId) identity;
    //CSJ,field=identityHandle,識別情報ハンドル
    private SafeFileHandle? identityHandle;

    private OwnedStagingDirectory(string path, List<SafeFileHandle> ancestors, SafeFileHandle stagePin, (ulong Volume, Guid FileId) identity)
    {
        Path = path;
        this.ancestors = ancestors;
        this.stagePin = stagePin;
        this.identity = identity;
    }

    //CSJ,property=Path,パス
    internal string Path { get; }

    internal static OwnedStagingDirectory Reserve(string parent, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("A staging prefix must be one filename component.", nameof(prefix));
        parent = System.IO.Path.GetFullPath(parent);
        //CSJ,variable=paths,パス一覧
        var paths = new Stack<string>();
        for (var cursor = new DirectoryInfo(parent); cursor is not null; cursor = cursor.Parent)
            paths.Push(cursor.FullName);
        var ancestors = new List<SafeFileHandle>();
        //CSJ,variable=stage,作業段階
        SafeFileHandle? stage = null;
        try
        {
            while (paths.TryPop(out var path))
            {
                var ancestor = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: true, allowRename: false);
                ancestors.Add(ancestor);
            }

            //CSJ,variable=name,名前
            var name = prefix + Guid.NewGuid().ToString("N");
            stage = CreateAndPin(ancestors[^1], name);
            //CSJ,variable=owned,管理対象の
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

    //CSJ,method=Publish,確定反映する
    internal void Publish(string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        destination = System.IO.Path.GetFullPath(destination);
        if (!string.Equals(System.IO.Path.GetDirectoryName(destination), System.IO.Path.GetDirectoryName(Path), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(destination, Path, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A staging directory can only be published to a different name under the reserved parent.", nameof(destination));
        //CSJ,variable=publication,確定反映
        using var publication = OpenForPublication();
        cancellationToken.ThrowIfCancellationRequested();
        OwnedFilePublication.RenameOpenedPath(publication, destination);
    }

    internal bool DeleteIfEmpty()
    {
        //CSJ,variable=publication,確定反映
        using var publication = OpenForPublication();
        return OwnedFilePublication.TryDeleteOpenedPath(publication);
    }

    //CSJ,method=PinRecoveryPath,固定参照復旧パス
    internal SafeFileHandle PinRecoveryPath()
    {
        var handle = OwnedFilePublication.OpenOrdinaryPath(Path, expectDirectory: true, allowRename: false,
            FileShare.ReadWrite, enforceDirectoryShare: true);
        try
        {
            if (ProjectFileEvidence.ReadPhysicalIdentity(handle) != identity)
                throw new IOException("Staging directory identity changed.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    //CSJ,method=CheckRecoveryPath,復旧パスを確認する
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
        //CSJ,variable=publication,確定反映
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

    //CSJ,method=Dispose,破棄する
    public void Dispose()
    {
        identityHandle?.Dispose();
        stagePin.Dispose();
        DisposeAncestors(ancestors);
    }

    private static void DisposeAncestors(List<SafeFileHandle> handles)
    {
        //CSJ,variable=index,インデックス
        for (var index = handles.Count - 1; index >= 0; index--) handles[index].Dispose();
    }

    internal static SafeFileHandle CreateAndPin(SafeFileHandle parent, string name)
    {
        if (!AssetInventory.IsOrdinaryPathComponent(name))
            throw new ArgumentException("A staging directory name must be one ordinary path component.", nameof(name));
        //CSJ,variable=text,テキスト
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
            //CSJ,variable=attributes,属性
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = namePointer,
                Attributes = CaseInsensitive | DontReparse,
            };
            // FILE_CREATE creates and returns a handle to the same directory in a
            // single call. A collision or reparse must fail before Git is started.
            //CSJ,variable=status,状態
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

    //CSJ,struct=UnicodeString,Unicode文字列
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        //CSJ,field=Length,長さ
        internal ushort Length;
        //CSJ,field=MaximumLength,最大の長さ
        internal ushort MaximumLength;
        //CSJ,field=Buffer,バッファー
        internal IntPtr Buffer;
    }

    //CSJ,struct=ObjectAttributes,オブジェクト属性
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        //CSJ,field=Length,長さ
        internal uint Length;
        //CSJ,field=RootDirectory,ルートディレクトリ
        internal IntPtr RootDirectory;
        //CSJ,field=ObjectName,オブジェクト名前
        internal IntPtr ObjectName;
        //CSJ,field=Attributes,属性
        internal uint Attributes;
        //CSJ,field=SecurityDescriptor,セキュリティ記述情報
        internal IntPtr SecurityDescriptor;
        internal IntPtr SecurityQualityOfService;
    }

    //CSJ,struct=IoStatusBlock,I/O状態ブロック
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        //CSJ,field=Status,状態
        internal IntPtr Status;
        //CSJ,field=Information,情報
        internal UIntPtr Information;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        //CSJ,parameter=status,状態
        out IoStatusBlock status, IntPtr allocationSize, uint fileAttributes, FileShare share, uint disposition, uint options,
        IntPtr extendedAttributes, uint extendedAttributesLength);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
