using System.ComponentModel;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using static YMMProjectManager.Infrastructure.OwnedFilePublication;

namespace YMMProjectManager.Infrastructure.Generations;

internal static class ProjectGenerationRestorePublication
{
    internal static FileStream? OpenTarget(string path)
    {
        try
        {
            var handle = OpenOrdinaryPath(path, expectDirectory: false, allowRename: true, FileShare.None);
            try { return new FileStream(handle, FileAccess.Read); }
            catch { handle.Dispose(); throw; }
        }
        catch (IOException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 2 or 3 })
        {
            return null;
        }
    }

    internal static FileStream CreateCandidate(string path, FileStream? original, bool copyInheritedRules = false)
    {
        if (original is null) return CreateOwnedOutputFile(path, readBack: true);

        // Match permissions at creation so even a staged PF has the original's access rules.
        var security = original.GetAccessControl();
        if (!copyInheritedRules && !security.AreAccessRulesProtected && security.GetAccessRules(includeExplicit: true, includeInherited: false,
            typeof(System.Security.Principal.SecurityIdentifier)).Count == 0)
            return CreateOwnedOutputFile(path, readBack: true);
        return new FileInfo(path).Create(FileMode.CreateNew,
            FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Delete,
            FileShare.None, 4096, FileOptions.Asynchronous, security);
    }

    // Both file handles and the destination parent stay open from verification through publication.
    // The original handle owns the exact version taken out of the way, including its metadata.
    internal static async Task<string?> PublishAsync(FileStream candidate, FileStream? original, string targetPath,
        string? backupPath, CancellationToken token, Action? afterParking = null)
    {
        token.ThrowIfCancellationRequested();
        if (original is null)
        {
            RenameOpenedPath(candidate.SafeFileHandle, targetPath);
            return null;
        }

        ProjectListReplacement.VerifyCompatibleFileMetadata(original, candidate, targetPath, allowAclCreationMarkerDifference: true);

        string? backup = string.IsNullOrWhiteSpace(backupPath) ? null : Path.GetFullPath(backupPath);
        if (string.Equals(backup, targetPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("元のプロジェクトと同じ場所にバックアップを保存できません。");
        var parkedPath = Path.Combine(Path.GetDirectoryName(targetPath)!,
            $"{Guid.NewGuid():N}_before-restore{Path.GetExtension(targetPath)}");
        DirectoryFence? backupFence = null;
        OwnedStagingDirectory? recoveryDirectory = null;
        Microsoft.Win32.SafeHandles.SafeFileHandle? recoveryPin = null;
        FileStream? backupCopy = null;
        var backupPublished = false;
        var parked = false;
        try
        {
            if (backup is not null)
            {
                var backupDirectory = Path.GetDirectoryName(backup)!;
                backupFence = ProjectGenerationStorage.OpenOrCreateOutputDirectory(backupDirectory);
                using var backupParent = OpenOrdinaryPath(backupDirectory, expectDirectory: true, allowRename: false, FileShare.ReadWrite,
                    enforceDirectoryShare: true);
                if (ProjectFileEvidence.ReadPhysicalIdentity(backupParent).Volume == ProjectFileEvidence.ReadPhysicalIdentity(original).Volume)
                {
                    parkedPath = backup;
                }
                else
                {
                    // Keep the exact original beside the project across volumes. The ordinary backup
                    // location also gets a verified copy of the original project's data stream.
                    recoveryDirectory = OwnedStagingDirectory.Reserve(Path.GetDirectoryName(targetPath)!, ".ymm-restore-recovery-");
                    recoveryPin = recoveryDirectory.PinRecoveryPath();
                    parkedPath = Path.Combine(recoveryDirectory.Path, Path.GetFileName(targetPath));
                    var backupTemp = Path.Combine(backupDirectory, $"{Guid.NewGuid():N}.backup.tmp");
                    backupCopy = CreateCandidate(backupTemp, original, copyInheritedRules: true);
                    ProjectListReplacement.VerifyCompatibleFileSecurity(original, backupCopy, allowAclCreationMarkerDifference: true);
                    original.Position = 0;
                    var expected = await SHA256.HashDataAsync(original, token).ConfigureAwait(false);
                    original.Position = 0;
                    await original.CopyToAsync(backupCopy, token).ConfigureAwait(false);
                    await backupCopy.FlushAsync(token).ConfigureAwait(false);
                    backupCopy.Flush(flushToDisk: true);
                    backupCopy.Position = 0;
                    var actual = await SHA256.HashDataAsync(backupCopy, token).ConfigureAwait(false);
                    if (!expected.SequenceEqual(actual))
                        throw new IOException("復元前バックアップの SHA256 検証に失敗しました。");
                    token.ThrowIfCancellationRequested();
                    RenameOpenedPath(backupCopy.SafeFileHandle, backup);
                    backupPublished = true;
                }
            }

            token.ThrowIfCancellationRequested();
            RenameOpenedPath(original.SafeFileHandle, parkedPath);
            parked = true;
            afterParking?.Invoke();
            token.ThrowIfCancellationRequested();
            RenameOpenedPath(candidate.SafeFileHandle, targetPath);
            if (backup is null && TryDeleteOpenedPath(original.SafeFileHandle))
                return null;
            return parkedPath;
        }
        catch (Exception error) when (parked)
        {
            try
            {
                RenameOpenedPath(original.SafeFileHandle, targetPath);
                parked = false;
            }
            catch (IOException rollbackError)
            {
                throw new IOException($"元の場所へ戻せませんでした。元のプロジェクトを次の場所に保全しました。\n{parkedPath}",
                    new AggregateException(error, rollbackError));
            }
            throw;
        }
        finally
        {
            if (backupCopy is not null)
            {
                if (!backupPublished) TryDeleteOpenedPath(backupCopy.SafeFileHandle);
                backupCopy.Dispose();
            }
            recoveryPin?.Dispose();
            if (recoveryDirectory is not null)
            {
                try
                {
                    if (!parked) recoveryDirectory.DeleteIfEmpty();
                }
                catch (IOException) { /* Keep a recovery directory whose ownership cannot be verified. */ }
                catch (UnauthorizedAccessException) { }
                finally { recoveryDirectory.Dispose(); }
            }
            backupFence?.Dispose();
        }
    }
}
