using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace YMMProjectManager.Infrastructure;

//CSJ,class=OwnedFilePublication,管理対象ファイルの確定反映
internal static class OwnedFilePublication
{
    //CSJ,class=DirectoryFence,ディレクトリ差し替え防止
    internal sealed class DirectoryFence(bool enforceShare = false) : IDisposable
    {
        private readonly List<SafeFileHandle> handles = [];

        //CSJ,method=Open,開く
        public static DirectoryFence Open(string path, bool enforceShare = false)
        {
            var ancestors = new Stack<string>();
            for (var cursor = new DirectoryInfo(path); cursor is not null; cursor = cursor.Parent)
                ancestors.Push(cursor.FullName);
            //CSJ,variable=fence,整合性境界
            var fence = new DirectoryFence(enforceShare);
            try
            {
                while (ancestors.TryPop(out var directory)) fence.Add(directory);
                return fence;
            }
            catch
            {
                fence.Dispose();
                throw;
            }
        }

        //CSJ,method=Add,追加する
        public SafeFileHandle Add(string path)
        {
            // Child publication needs a writable directory handle. Deny delete access to the
            // directory itself while permitting those creates and renames of owned children.
            var share = enforceShare ? FileShare.ReadWrite : FileShare.Read;
            var handle = OpenOrdinaryPath(path, expectDirectory: true, allowRename: false, share, enforceDirectoryShare: enforceShare);
            handles.Add(handle);
            return handle;
        }

        //CSJ,method=Dispose,破棄する
        public void Dispose()
        {
            for (var i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
            handles.Clear();
        }
    }

    //CSJ,method=CreateOwnedOutputFile,管理対象の出力ファイルを作成する
    internal static FileStream CreateOwnedOutputFile(string path, bool readBack = false)
    {
        const uint genericRead = 0x80000000;
        const uint genericWrite = 0x40000000;
        //CSJ,variable=delete,削除
        const uint delete = 0x10000;
        //CSJ,variable=createNew,作成新規
        const uint createNew = 1;
        const uint openWithoutFollowingLink = 0x00200000;
        const uint overlapped = 0x40000000;
        //CSJ,variable=access,アクセス
        var access = genericWrite | delete | (readBack ? genericRead : 0);
        var handle = OpenPath(ExtendedPath(path), access, FileShare.None, IntPtr.Zero,
            createNew, openWithoutFollowingLink | overlapped, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            try { return new FileStream(handle, readBack ? FileAccess.ReadWrite : FileAccess.Write, bufferSize: 1, isAsync: true); }
            catch
            {
                TryDeleteOpenedPath(handle);
                handle.Dispose();
                throw;
            }
        }
        //CSJ,variable=error,エラー
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException("一時ファイルを作成できません。", new Win32Exception(error));
    }

    //CSJ,method=OpenOrdinaryPath,通常パスを開く
    internal static SafeFileHandle OpenOrdinaryPath(string path, bool expectDirectory, bool allowRename, FileShare share = FileShare.Read,
        bool enforceDirectoryShare = false)
    {
        //CSJ,variable=listDirectory,一覧ディレクトリ
        const uint listDirectory = 1;
        //CSJ,variable=readAttributes,読み取り属性
        const uint readAttributes = 0x80;
        const uint genericRead = 0x80000000;
        //CSJ,variable=delete,削除
        const uint delete = 0x10000;
        //CSJ,variable=openExisting,表示既存
        const uint openExisting = 3;
        const uint openDirectoryWithoutFollowingLink = 0x02200000;
        //CSJ,variable=fileAttributeTagInfo,ファイル属性タグ情報
        const int fileAttributeTagInfo = 9;
        // Attribute-only access does not enforce a handle's sharing mode. Listing access is needed
        // when a caller must prevent the directory itself from being deleted or renamed.
        //CSJ,variable=access,アクセス
        var access = (expectDirectory ? readAttributes | (enforceDirectoryShare ? listDirectory : 0) : genericRead) |
            (allowRename ? delete : 0);
        var handle = OpenPath(ExtendedPath(path), access, share,
            IntPtr.Zero, openExisting, openDirectoryWithoutFollowingLink, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            //CSJ,variable=error,エラー
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("保存先を保護できません。", new Win32Exception(error));
        }
        if (!GetFileInformationByHandleEx(handle, fileAttributeTagInfo, out var info, 8) ||
            (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != (expectDirectory ? FileAttributes.Directory : 0))
        {
            handle.Dispose();
            throw new IOException("通常のファイルまたはフォルダーを使用してください。リンクは利用できません。");
        }
        return handle;
    }

    internal static string ExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    internal static void RenameOpenedPath(SafeFileHandle handle, string final, bool replaceExisting = false)
    {
        const int fileRenameInfo = 3;
        // FILE_RENAME_INFO has a native-sized RootDirectory and a trailing UTF-16 name.
        // A null RootDirectory uses the complete destination path.
        //CSJ,variable=name,名前
        var name = Encoding.Unicode.GetBytes(ExtendedPath(final));
        //CSJ,variable=nameLengthOffset,名前長さオフセット
        var nameLengthOffset = 2 * IntPtr.Size;
        //CSJ,variable=nameOffset,名前オフセット
        var nameOffset = nameLengthOffset + sizeof(int);
        //CSJ,variable=data,データ
        var data = new byte[nameOffset + name.Length + sizeof(char)];
        data[0] = replaceExisting ? (byte)1 : (byte)0;
        BitConverter.TryWriteBytes(data.AsSpan(nameLengthOffset), name.Length);
        name.CopyTo(data, nameOffset);
        if (!SetFileInformationByHandle(handle, fileRenameInfo, data, (uint)data.Length))
            throw new IOException("新しい名前で保存できません。", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    //CSJ,method=TryDeleteOpenedPath,表示済みのパスの削除を試みる
    internal static bool TryDeleteOpenedPath(SafeFileHandle handle)
    {
        //CSJ,variable=fileDispositionInfo,ファイル処置区分情報
        const int fileDispositionInfo = 4;
        // This marks only the opened file or empty directory for deletion.
        return SetFileInformationByHandle(handle, fileDispositionInfo, [1], 1);
    }

    //CSJ,struct=FileAttributeTagInfo,ファイル属性タグ情報
    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        //CSJ,field=Attributes,属性
        public FileAttributes Attributes;
        //CSJ,field=ReparseTag,再解析タグ
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileAttributeTagInfo information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, byte[] information, uint length);

    //CSJ,method=OpenPath,パスを開く
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenPath(string path, uint access, FileShare share, IntPtr securityAttributes,
        //CSJ,parameter=disposition,処置区分
        uint disposition, uint flags, IntPtr template);
}
