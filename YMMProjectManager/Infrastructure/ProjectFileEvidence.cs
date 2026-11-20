using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Infrastructure;

internal static class ProjectFileEvidence
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        internal ulong Volume;
        internal Guid FileId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileIdInfo info, uint size);

    internal static async Task<ProjectFileIdentity> ReadAsync(string path, CancellationToken token = default)
    {
        await using var stream = OpenRead(path);
        return await ReadAsync(stream, token).ConfigureAwait(false);
    }

    internal static FileStream OpenRead(string path)
    {
        var full = Path.GetFullPath(path);
        ProjectListDrafts.CheckOrdinaryDirectory(Path.GetDirectoryName(full)!);
        if ((File.GetAttributes(full) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Project path is not a regular file.");
        return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
    }

    internal static async Task<ProjectFileIdentity> ReadAsync(FileStream stream, CancellationToken token = default)
    {
        const int fileIdInformation = 18;
        if (!GetFileInformationByHandleEx(stream.SafeFileHandle, fileIdInformation, out var identity, (uint)Marshal.SizeOf<FileIdInfo>()) ||
            identity.FileId == Guid.Empty)
            throw new IOException("The filesystem did not provide a project file identity.");
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        return new ProjectFileIdentity(identity.Volume, identity.FileId, digest);
    }
}
