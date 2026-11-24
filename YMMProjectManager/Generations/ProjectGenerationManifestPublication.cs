using System.ComponentModel;
using System.IO;
using static YMMProjectManager.Infrastructure.OwnedFilePublication;

namespace YMMProjectManager.Infrastructure.Generations;

// The directory and process lease are held by the writer for the entire publication.
internal sealed class ProjectGenerationManifestPublication(string path)
{
    private string CandidatePath(Guid operation) => $"{path}.tmp-{operation:N}";
    private string PreviousPath(Guid operation) => $"{path}.previous-{operation:N}";

    internal sealed class PendingException(string previous, string candidate, Exception cause)
        : IOException($"世代 manifest を確定できませんでした。両方の版を保全しました。\n旧版 {previous}\n保存候補 {candidate}", cause);

    internal static InvalidDataException Changed() =>
        new("世代 manifest が変更されたため保存を中止しました。再読み込みしてください。");

    internal FileStream AcquireLease()
    {
        var leasePath = path + ".write-lock";
        try
        {
            // Created once and kept. Deleting it after each write would split waiting writers between files.
            try { return CreateOwnedOutputFile(leasePath); }
            catch (IOException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 80 or 183 })
            {
                return ProjectListReplacement.Open(leasePath, share: FileShare.None);
            }
        }
        catch (IOException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 32 or 33 })
        {
            throw new InvalidDataException("別のプロセスで世代 manifest を保存中です。再読み込みしてください。", ex);
        }
    }

    internal FileStream? OpenCurrent(FileShare share, bool allowRename = false)
    {
        try { return ProjectListReplacement.Open(path, canRename: allowRename, share: share); }
        catch (IOException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 2 or 3 })
        {
            var directory = Path.GetDirectoryName(path)!;
            var pattern = Path.GetFileName(path) + ".previous-*";
            string? previous;
            try { previous = Directory.EnumerateFileSystemEntries(directory, pattern).FirstOrDefault(); }
            catch (DirectoryNotFoundException) { previous = null; }
            if (previous is not null)
                throw new InvalidDataException($"世代 manifest の現用名が空で旧版が残っています。回復先を確認してください。\n{previous}");
            return null;
        }
    }

    internal FileStream CreateCandidate(Guid operation, ProjectListReplacement.FileVersion? expected)
    {
        using var original = OpenCurrent(FileShare.Read | FileShare.Delete);
        if ((original is null) != (expected is null) ||
            original is not null && ProjectFileEvidence.ReadPhysicalIdentity(original) != (expected!.Volume, expected.FileId))
            throw Changed();

        var candidate = ProjectGenerationRestorePublication.CreateCandidate(CandidatePath(operation), original);
        try
        {
            if (original is not null)
                ProjectListReplacement.VerifyCompatibleFileSecurity(original, candidate, allowAclCreationMarkerDifference: true);
            return candidate;
        }
        catch
        {
            TryDeleteOpenedPath(candidate.SafeFileHandle);
            candidate.Dispose();
            throw;
        }
    }

    internal void Publish(FileStream candidate, FileStream? original, Guid operation, CancellationToken token, Action? afterParking)
    {
        token.ThrowIfCancellationRequested();
        if (original is null)
        {
            RenameOpenedPath(candidate.SafeFileHandle, path);
            return;
        }

        ProjectListReplacement.VerifyCompatibleFileMetadata(original, candidate, path, allowAclCreationMarkerDifference: true);
        File.SetCreationTimeUtc(candidate.SafeFileHandle, File.GetCreationTimeUtc(original.SafeFileHandle));
        candidate.Flush(flushToDisk: true);
        token.ThrowIfCancellationRequested();

        // Hold the validated file exclusively right up to parking it. Once the live name is vacant,
        // a non-cooperating publisher may claim it, but neither rename can overwrite that file.
        var previous = PreviousPath(operation);
        RenameOpenedPath(original.SafeFileHandle, previous);
        try
        {
            afterParking?.Invoke();
            token.ThrowIfCancellationRequested();
            RenameOpenedPath(candidate.SafeFileHandle, path);
        }
        catch (Exception error)
        {
            try { RenameOpenedPath(original.SafeFileHandle, path); }
            catch (IOException rollbackError)
            {
                throw new PendingException(previous, CandidatePath(operation), new AggregateException(error, rollbackError));
            }
            throw;
        }

        // Publication is already committed. A failed cleanup leaves the previous version for recovery.
        TryDeleteOpenedPath(original.SafeFileHandle);
    }
}
