using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YMMProjectManager.Infrastructure;

/// <summary>Records and resolves the gap between parking the current list and publishing the new one.</summary>
internal sealed class ProjectListReplacement(string listPath)
{
    internal enum RecoveryState { ActivePreserved, Published, CanRestore, CanRestoreExternal, Conflict, Unverifiable }
    internal sealed record Recovery(RecoveryState State, string ActivePath, string ParkedPath, string CandidatePath, string JournalPath,
        Journal Proof, FileVersion? ActiveVersion, FileVersion? ParkedVersion);
    internal sealed record FileVersion(ulong Volume, Guid FileId, string Sha256);
    internal sealed record Journal(int Version, Guid Operation, FileVersion Original, FileVersion Result);
    private sealed record Envelope(byte[] Payload, string Sha256);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    internal string JournalPath => listPath + ".publication-pending";
    internal string ParkedPath(Guid operation) => $"{listPath}.parked-{operation:N}";
    internal string CandidatePath(Guid operation) => $"{listPath}.tmp-{operation:N}";
    private string ClosedPath(Guid operation, bool external) =>
        $"{listPath}.publication-{operation:N}." + (external ? "closed-external" : "closed");

    internal bool HasExternalResolution(Guid operation) => Exists(ClosedPath(operation, external: true));

    internal void RequireLocalNtfs()
    {
        var root = Path.GetPathRoot(listPath);
        if (root is not { Length: 3 } || !char.IsAsciiLetter(root[0]) || root[1] != ':')
            throw new IOException("Recoverable project list publication requires a fixed local NTFS drive.");
        var drive = new DriveInfo(root);
        if (drive.DriveType != DriveType.Fixed || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Recoverable project list publication requires a fixed local NTFS drive.");
    }

    internal bool IsPending() => Exists(JournalPath);

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal void RequireNoPending()
    {
        if (IsPending())
            throw new InvalidDataException($"An interrupted project list save needs explicit recovery. Evidence: {JournalPath}");
    }

    internal async Task<FileStream> WriteAsync(Journal journal, CancellationToken token)
    {
        var temporary = $"{JournalPath}-{journal.Operation:N}.tmp";
        var stream = OwnedFilePublication.CreateOwnedOutputFile(temporary);
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions);
            var evidence = new Envelope(payload, Convert.ToHexString(SHA256.HashData(payload)));
            await JsonSerializer.SerializeAsync(stream, evidence, JsonOptions, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            token.ThrowIfCancellationRequested();
            OwnedFilePublication.RenameOpenedPath(stream.SafeFileHandle, JournalPath);
            return stream;
        }
        catch
        {
            OwnedFilePublication.TryDeleteOpenedPath(stream.SafeFileHandle);
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<Recovery?> InspectAsync(CancellationToken token)
    {
        if (!IsPending()) return null;
        RequireLocalNtfs();
        using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(listPath)!);
        using var marker = Open(JournalPath);
        var journal = await ReadAsync(marker, token).ConfigureAwait(false);
        using var active = TryOpen(listPath);
        return await ObserveAsync(journal, active, token).ConfigureAwait(false);
    }

    internal async Task<Recovery> ResolveAsync(Recovery confirmed, CancellationToken token)
    {
        RequireLocalNtfs();
        using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(listPath)!);
        using var marker = Open(JournalPath, canRename: true);
        var journal = await ReadAsync(marker, token).ConfigureAwait(false);
        // An active file that the user chose to keep cannot be renamed or written until the journal is archived.
        using var active = TryOpen(listPath);
        var observed = await ObserveAsync(journal, active, token).ConfigureAwait(false);
        if (observed != confirmed || observed.State is RecoveryState.Unverifiable)
            throw new InvalidDataException("The interrupted publication no longer has the confirmed evidence. All versions were retained.");
        var external = observed.State is RecoveryState.CanRestoreExternal or RecoveryState.Conflict;
        var closedPath = ClosedPath(journal.Operation, external);
        if (observed.State is RecoveryState.CanRestore or RecoveryState.CanRestoreExternal)
        {
            using var parked = Open(observed.ParkedPath, canRename: true);
            if (observed.ParkedVersion is null || !await MatchesAsync(parked, observed.ParkedVersion, token).ConfigureAwait(false))
                throw new InvalidDataException("The parked project list changed. Evidence was retained.");
            token.ThrowIfCancellationRequested();
            OwnedFilePublication.RenameOpenedPath(parked.SafeFileHandle, listPath);
            // The original is already restored; persist the outcome even when cancellation arrives here.
            OwnedFilePublication.RenameOpenedPath(marker.SafeFileHandle, closedPath);
        }
        else
        {
            token.ThrowIfCancellationRequested();
            OwnedFilePublication.RenameOpenedPath(marker.SafeFileHandle, closedPath);
        }
        // Keep all evidence when a user resolves an interrupted save.
        return observed;
    }

    private async Task<Recovery> ObserveAsync(Journal journal, FileStream? active, CancellationToken token)
    {
        var parkedPath = ParkedPath(journal.Operation);
        using var parked = TryOpen(parkedPath);
        var parkedVersion = parked is null ? null : await ReadFileVersionAsync(parked, token).ConfigureAwait(false);
        var activeVersion = active is null ? null : await ReadFileVersionAsync(active, token).ConfigureAwait(false);
        var state = (activeVersion, parkedVersion) switch
        {
            (null, null) => RecoveryState.Unverifiable,
            (null, _) => parkedVersion == journal.Original ? RecoveryState.CanRestore : RecoveryState.CanRestoreExternal,
            _ when activeVersion == journal.Result => RecoveryState.Published,
            _ when activeVersion == journal.Original => RecoveryState.ActivePreserved,
            _ => RecoveryState.Conflict,
        };
        return new Recovery(state, listPath, parkedPath, CandidatePath(journal.Operation), JournalPath, journal, activeVersion, parkedVersion);
    }

    internal static FileStream Open(string path, bool canRename = false, FileShare share = FileShare.Read)
    {
        var handle = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename: canRename, share);
        try { return new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }

    internal static FileStream? TryOpen(string path, FileShare share = FileShare.Read)
    {
        try { return Open(path, share: share); }
        catch (IOException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 })
        { return null; }
    }

    internal static void VerifyCompatibleFileSecurity(FileStream original, FileStream candidate)
    {
        // The new file inherits from its directory. Refuse to write list bytes until its security is known to match.
        const AccessControlSections sections = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
        if (original.GetAccessControl().GetSecurityDescriptorSddlForm(sections) !=
            candidate.GetAccessControl().GetSecurityDescriptorSddlForm(sections))
            throw new InvalidDataException("The staged list does not have the original file permissions.");
    }

    internal static void VerifyCompatibleFileMetadata(FileStream original, FileStream candidate, string parkedPath)
    {
        // A rename does not inherit the displaced file's metadata as File.Replace did.
        VerifyCompatibleFileSecurity(original, candidate);
        const FileAttributes ordinary = FileAttributes.Archive | FileAttributes.Normal;
        if ((File.GetAttributes(original.SafeFileHandle) & ~ordinary) != 0 ||
            (File.GetAttributes(candidate.SafeFileHandle) & ~ordinary) != 0)
            throw new InvalidDataException("Project list replacement requires ordinary files without custom file attributes.");

        using var find = FindFirstStream(OwnedFilePublication.ExtendedPath(parkedPath), 0, out var stream, 0);
        if (find.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 38) return; // ERROR_HANDLE_EOF: no streams.
            throw new IOException("Could not verify the project's file streams.", new Win32Exception(error));
        }
        if (stream.Name != "::$DATA" || FindNextStream(find, out _))
            throw new InvalidDataException("The project list has additional file streams that a rename cannot preserve.");
        var streamError = Marshal.GetLastWin32Error();
        if (streamError != 38)
            throw new IOException("Could not verify the project's file streams.", new Win32Exception(streamError));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamInfo
    {
        private long length;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] internal string Name;
    }

    private sealed class StreamFindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private StreamFindHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => FindClose(handle);
    }

    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern StreamFindHandle FindFirstStream(string path, int level, out StreamInfo data, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStream(StreamFindHandle find, out StreamInfo data);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr find);

    private static async Task<Journal> ReadAsync(FileStream stream, CancellationToken token)
    {
        if (stream.Length is <= 0 or > 4096)
            throw new InvalidDataException("Invalid interrupted project list journal length.");
        var envelope = await JsonSerializer.DeserializeAsync<Envelope>(stream, JsonOptions, token).ConfigureAwait(false);
        if (envelope?.Payload is null || envelope.Sha256 != Convert.ToHexString(SHA256.HashData(envelope.Payload)))
            throw new InvalidDataException("Invalid interrupted project list journal checksum.");
        var record = JsonSerializer.Deserialize<Journal>(envelope.Payload, JsonOptions);
        if (record is not { Version: 1 } || record.Operation == Guid.Empty || !IsValid(record.Original) || !IsValid(record.Result) ||
            record.Original == record.Result)
            throw new InvalidDataException("Invalid interrupted project list journal.");
        return record;
    }

    private static bool IsValid(FileVersion? version) => version is { Sha256.Length: 64 } &&
        version.FileId != Guid.Empty && version.Sha256.All(Uri.IsHexDigit);

    internal static async Task<FileVersion> ReadFileVersionAsync(FileStream stream, CancellationToken token)
    {
        var (volume, id) = ProjectFileEvidence.ReadPhysicalIdentity(stream);
        stream.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        return new FileVersion(volume, id, hash);
    }

    internal static async Task<bool> MatchesAsync(FileStream stream, FileVersion expected, CancellationToken token)
    {
        var (volume, id) = ProjectFileEvidence.ReadPhysicalIdentity(stream);
        return volume == expected.Volume && id == expected.FileId &&
            await ReadFileVersionAsync(stream, token).ConfigureAwait(false) == expected;
    }
}
