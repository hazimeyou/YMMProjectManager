using System.IO;
using System.Text.Json;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Settings;

public sealed partial class YMMProjectManagerSettings
{
    private const string PreviousSettingsSuffix = ".previous-";
    private const string SettingsReceiptSuffix = ".recovery-";
    private const int SettingsReceiptVersion = 1;
    private const int MaxSettingsReceiptBytes = 4096;

    internal sealed record SettingsRecoveryCandidate(Guid OperationId, string BackupPath, ulong Volume, Guid FileId, string Sha256);
    private sealed record SettingsRecoveryReceipt(int Version, Guid OperationId, string TargetName, ulong DirectoryVolume,
        Guid DirectoryId, ulong Volume, Guid FileId, string Sha256);

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
                return InspectRecovery(operationId.Value, receipt, backup, out _);
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
                if (InspectRecovery(candidate.OperationId, receipt, backup, out var folders) != candidate)
                    return false;

                beforePublish?.Invoke();
                // This move cannot replace a file that appears after the missing-name check.
                OwnedFilePublication.RenameOpenedPath(backup.SafeFileHandle, settingsPath);
                confirmedVersion = new((candidate.Volume, candidate.FileId), candidate.Sha256);
                SearchFolders = folders;
                hasConfirmedSettings = true;
                RetainedBackupPath = null;
                OwnedFilePublication.TryDeleteOpenedPath(receipt.SafeFileHandle);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                return false;
            }
        }
    }

    private FileStream PrepareRecoveryReceipt(Guid operationId, SettingsVersion original, (ulong Volume, Guid Id) directory)
    {
        var data = new SettingsRecoveryReceipt(SettingsReceiptVersion, operationId, Path.GetFileName(settingsPath),
            directory.Volume, directory.Id, original.Identity.Item1, original.Identity.Item2, original.Sha256);
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

    private SettingsRecoveryCandidate InspectRecovery(Guid operationId, FileStream receipt, FileStream backup, out List<string> folders)
    {
        if (receipt.Length is <= 0 or > MaxSettingsReceiptBytes)
            throw new InvalidDataException("Invalid settings recovery receipt size.");
        var evidence = JsonSerializer.Deserialize<SettingsRecoveryReceipt>(receipt);
        using var parent = OwnedFilePublication.OpenOrdinaryPath(Path.GetDirectoryName(settingsPath)!, expectDirectory: true, allowRename: false);
        var parentId = ProjectFileEvidence.ReadPhysicalIdentity(parent);
        var bytes = ReadSettings(backup);
        var version = GetVersion(backup, bytes);
        if (evidence?.Version != SettingsReceiptVersion || evidence.OperationId != operationId ||
            !string.Equals(evidence.TargetName, Path.GetFileName(settingsPath), StringComparison.OrdinalIgnoreCase) ||
            (evidence.DirectoryVolume, evidence.DirectoryId) != parentId ||
            evidence.Volume != version.Identity.Item1 || evidence.FileId != version.Identity.Item2 ||
            !string.Equals(evidence.Sha256, version.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("Settings recovery evidence no longer matches the retained file.");

        var data = DecodeSettings(bytes) ?? throw new InvalidDataException("Invalid retained settings.");
        folders = NormalizeFolders(data.SearchFolders ?? []);
        return new(operationId, PreviousSettingsPath(operationId), evidence.Volume, evidence.FileId, version.Sha256);
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

    private static FileStream OpenSettingsFile(string path, bool allowRename)
    {
        var handle = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename);
        try { return new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }
}
