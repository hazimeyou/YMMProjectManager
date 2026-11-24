using System.IO;
using Microsoft.Win32.SafeHandles;

namespace YMMProjectManager.Infrastructure.Output;

internal static partial class ThumbnailCacheMaintenance
{
    internal static void ReadVerifiedFrames(string path, long maxFrameBytes, Action<int, SafeFileHandle> read, CancellationToken token)
    {
        using var fence = OwnedFilePublication.DirectoryFence.Open(path, enforceShare: true);
        using var directory = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: true, allowRename: false,
            FileShare.ReadWrite, enforceDirectoryShare: true);
        using var files = PinExactCapture(path, ProjectFileEvidence.ReadPhysicalIdentity(directory),
            allowDelete: false, verifyContents: true, token: token, maxFrameBytes: maxFrameBytes);
        for (var slot = 0; slot < ThumbnailCachePublication.SlotCount; slot++)
        {
            token.ThrowIfCancellationRequested();
            // PinExactCapture opens the completion marker and receipt before the numbered frames.
            read(slot, files.Handles[slot + 2]);
        }
    }
}
