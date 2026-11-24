using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Settings;

public sealed partial class YMMProjectManagerSettings
{
    private const string PreviousSettingsSuffix = ".previous-";
    private const string SettingsReceiptSuffix = ".recovery-";
    private const int SettingsReceiptVersion = 1;
    private const int MaxSettingsReceiptBytes = 4096;

    internal sealed record SettingsRecoveryCandidate(Guid OperationId, string BackupPath, ulong Volume, Guid FileId, string Sha256,
        ulong StagedVolume, Guid StagedId, string StagedSha256);
    private sealed record SettingsRecoveryReceipt(int Version, Guid OperationId, string TargetName, ulong DirectoryVolume,
        Guid DirectoryId, ulong Volume, Guid FileId, string Sha256, ulong StagedVolume, Guid StagedId, string StagedSha256);

    internal SettingsRecoveryCandidate? FindInterruptedSave()
    {
        lock (SaveLock)
        {
            try
            {
                using var directory = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(settingsPath)!, enforceShare: true);
                var operationId = FindMissingSettingsOperation();
                if (operationId is null) return null;
                using var receipt = OpenSettingsFile(SettingsReceiptPath(operationId.Value), allowRename: false);
                using var backup = OpenSettingsFile(PreviousSettingsPath(operationId.Value), allowRename: false);
                using var staged = OpenSettingsFile(StagedSettingsPath(operationId.Value), allowRename: false);
                return InspectRecovery(operationId.Value, receipt, backup, staged, out _);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                return null;
            }
        }
    }

    internal bool TryRecoverInterruptedSave(SettingsRecoveryCandidate candidate)
    {
        lock (SaveLock)
        {
            try
            {
                using var directory = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(settingsPath)!, enforceShare: true);
                if (FindMissingSettingsOperation() != candidate.OperationId) return false;
                using var receipt = OpenSettingsFile(SettingsReceiptPath(candidate.OperationId), allowRename: true);
                using var backup = OpenSettingsFile(PreviousSettingsPath(candidate.OperationId), allowRename: true);
                using var staged = OpenSettingsFile(StagedSettingsPath(candidate.OperationId), allowRename: true);
                if (InspectRecovery(candidate.OperationId, receipt, backup, staged, out var folders) != candidate)
                    return false;

                beforePublish?.Invoke();
                // This move cannot replace a file that appears after the missing-name check.
                OwnedFilePublication.RenameOpenedPath(backup.SafeFileHandle, settingsPath);
                confirmedVersion = new((candidate.Volume, candidate.FileId), candidate.Sha256);
                SearchFolders = folders;
                hasConfirmedSettings = true;
                RetainedBackupPath = null;
                OwnedFilePublication.TryDeleteOpenedPath(staged.SafeFileHandle);
                OwnedFilePublication.TryDeleteOpenedPath(receipt.SafeFileHandle);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                return false;
            }
        }
    }

    private FileStream PrepareRecoveryReceipt(Guid operationId, SettingsVersion original, SettingsVersion staged,
        (ulong Volume, Guid Id) directory)
    {
        var data = new SettingsRecoveryReceipt(SettingsReceiptVersion, operationId, Path.GetFileName(settingsPath),
            directory.Volume, directory.Id, original.Identity.Item1, original.Identity.Item2, original.Sha256,
            staged.Identity.Item1, staged.Identity.Item2, staged.Sha256);
        var receipt = OwnedFilePublication.CreateOwnedOutputFile(SettingsReceiptPath(operationId));
        try
        {
            JsonSerializer.Serialize(receipt, data);
            receipt.Flush(flushToDisk: true);
            return receipt;
        }
        catch
        {
            OwnedFilePublication.TryDeleteOpenedPath(receipt.SafeFileHandle);
            receipt.Dispose();
            throw;
        }
    }

    private SettingsRecoveryCandidate InspectRecovery(Guid operationId, FileStream receipt, FileStream backup, FileStream staged,
        out List<string> folders)
    {
        if (receipt.Length is <= 0 or > MaxSettingsReceiptBytes)
            throw new InvalidDataException("Invalid settings recovery receipt size.");
        var evidence = JsonSerializer.Deserialize<SettingsRecoveryReceipt>(receipt);
        using var parent = OwnedFilePublication.OpenOrdinaryPath(Path.GetDirectoryName(settingsPath)!, expectDirectory: true, allowRename: false);
        var parentId = ProjectFileEvidence.ReadPhysicalIdentity(parent);
        if (evidence?.Version != SettingsReceiptVersion || evidence.OperationId != operationId ||
            !string.Equals(evidence.TargetName, Path.GetFileName(settingsPath), StringComparison.OrdinalIgnoreCase) ||
            (evidence.DirectoryVolume, evidence.DirectoryId) != parentId ||
            (evidence.Volume, evidence.FileId) != ProjectFileEvidence.ReadPhysicalIdentity(backup) ||
            (evidence.StagedVolume, evidence.StagedId) != ProjectFileEvidence.ReadPhysicalIdentity(staged))
            throw new InvalidDataException("Settings recovery evidence no longer matches the retained files.");

        var bytes = ReadSettings(backup);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var stagedHash = Convert.ToHexString(SHA256.HashData(staged));
        if (hash != evidence.Sha256 || stagedHash != evidence.StagedSha256)
            throw new InvalidDataException("Settings recovery evidence no longer matches the retained contents.");

        var data = DecodeSettings(bytes) ?? throw new InvalidDataException("Invalid retained settings.");
        folders = NormalizeFolders(data.SearchFolders ?? []);
        return new(operationId, PreviousSettingsPath(operationId), evidence.Volume, evidence.FileId, hash,
            evidence.StagedVolume, evidence.StagedId, stagedHash);
    }

    private Guid? FindMissingSettingsOperation()
    {
        try
        {
            File.GetAttributes(settingsPath);
            return null;
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            var candidates = FindPreviousSettings();
            if (candidates.Length != 1) return null;
            var prefix = Path.GetFileName(settingsPath) + PreviousSettingsSuffix;
            var name = Path.GetFileName(candidates[0]);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            return Guid.TryParseExact(name.AsSpan(prefix.Length), "N", out var id) && id != Guid.Empty ? id : null;
        }
    }

    private string[] FindPreviousSettings()
    {
        var directory = Path.GetDirectoryName(settingsPath)!;
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFileSystemEntries(directory,
            Path.GetFileName(settingsPath) + PreviousSettingsSuffix + "*").Take(2).ToArray();
    }

    private string PreviousSettingsPath(Guid operationId) => settingsPath + PreviousSettingsSuffix + operationId.ToString("N");
    private string SettingsReceiptPath(Guid operationId) => settingsPath + SettingsReceiptSuffix + operationId.ToString("N");
    private string StagedSettingsPath(Guid operationId) => settingsPath + "." + operationId.ToString("N") + ".tmp";

    private static FileStream OpenSettingsFile(string path, bool allowRename)
    {
        var handle = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename);
        try { return new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }
}
