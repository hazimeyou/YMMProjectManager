using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Infrastructure;

//CSJ,class=ProjectFileEvidence,プロジェクトファイル証跡
internal static class ProjectFileEvidence
{
    //CSJ,struct=FileIdInfo,ファイルID情報
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        //CSJ,field=Volume,ボリューム
        internal ulong Volume;
        //CSJ,field=FileId,ファイルID
        internal Guid FileId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        //CSJ,parameter=info,情報
        out FileIdInfo info, uint size);

    //CSJ,method=ReadAsync,読み取る（非同期）
    internal static async Task<ProjectFileIdentity> ReadAsync(string path, CancellationToken token = default)
    {
        //CSJ,variable=stream,ストリーム
        await using var stream = OpenRead(path);
        return await ReadAsync(stream, token).ConfigureAwait(false);
    }

    //CSJ,method=OpenRead,読み取りを開く
    internal static FileStream OpenRead(string path)
    {
        //CSJ,variable=full,絶対パス
        var full = Path.GetFullPath(path);
        ProjectListDrafts.CheckOrdinaryDirectory(Path.GetDirectoryName(full)!);
        if ((File.GetAttributes(full) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Project path is not a regular file.");
        return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
    }

    //CSJ,method=ReadAsync,読み取る（非同期）
    internal static async Task<ProjectFileIdentity> ReadAsync(FileStream stream, CancellationToken token = default)
    {
        var (volume, id) = ReadPhysicalIdentity(stream);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        return new ProjectFileIdentity(volume, id, digest);
    }

    //CSJ,method=ReadPhysicalIdentity,物理識別情報を読み取る
    internal static (ulong Volume, Guid FileId) ReadPhysicalIdentity(FileStream stream) =>
        ReadPhysicalIdentity(stream.SafeFileHandle);

    //CSJ,method=ReadPhysicalIdentity,物理識別情報を読み取る
    internal static (ulong Volume, Guid FileId) ReadPhysicalIdentity(SafeFileHandle handle)
    {
        //CSJ,variable=fileIdInformation,ファイルID情報
        const int fileIdInformation = 18;
        if (!GetFileInformationByHandleEx(handle, fileIdInformation, out var identity, (uint)Marshal.SizeOf<FileIdInfo>()) ||
            identity.FileId == Guid.Empty)
            throw new IOException("The filesystem did not provide a project file identity.");
        return (identity.Volume, identity.FileId);
    }
}
