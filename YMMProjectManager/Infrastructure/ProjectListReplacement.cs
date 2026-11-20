using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YMMProjectManager.Infrastructure;

/// <summary>Records and resolves the gap between parking the current list and publishing the new one.</summary>
internal sealed class ProjectListReplacement(string listPath)
{
    internal enum RecoveryState { ActivePreserved, Published, CanRestore, Conflict, Unverifiable }
    internal sealed record Recovery(RecoveryState State, string ActivePath, string ParkedPath, string CandidatePath, string JournalPath,
        Journal Proof, FileVersion? ActiveVersion);
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
    private string ClosedPath(Guid operation) => $"{listPath}.publication-{operation:N}.closed";

    internal bool IsPending()
    {
        try { _ = File.GetAttributes(JournalPath); return true; }
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
        using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(listPath)!);
        using var marker = Open(JournalPath);
        var journal = await ReadAsync(marker, token).ConfigureAwait(false);
        using var active = TryOpen(listPath);
        return await ObserveAsync(journal, active, token).ConfigureAwait(false);
    }

    internal async Task<Recovery> ResolveAsync(Recovery confirmed, CancellationToken token)
    {
        using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(listPath)!);
        using var marker = Open(JournalPath, canRename: true);
        var journal = await ReadAsync(marker, token).ConfigureAwait(false);
        // An active file that the user chose to keep cannot be renamed or written until the journal is archived.
        using var active = TryOpen(listPath);
        var observed = await ObserveAsync(journal, active, token).ConfigureAwait(false);
        if (observed != confirmed || observed.State is RecoveryState.Unverifiable)
            throw new InvalidDataException("The interrupted publication no longer has the confirmed evidence. All versions were retained.");
        if (observed.State == RecoveryState.CanRestore)
        {
            using var parked = Open(observed.ParkedPath, canRename: true);
            if (!await MatchesAsync(parked, journal.Original, token).ConfigureAwait(false))
                throw new InvalidDataException("The parked project list changed. Evidence was retained.");
            token.ThrowIfCancellationRequested();
            OwnedFilePublication.RenameOpenedPath(parked.SafeFileHandle, listPath);
            // The original is already restored; persist the outcome even when cancellation arrives here.
            OwnedFilePublication.RenameOpenedPath(marker.SafeFileHandle, ClosedPath(journal.Operation));
        }
        else
        {
            token.ThrowIfCancellationRequested();
            OwnedFilePublication.RenameOpenedPath(marker.SafeFileHandle, ClosedPath(journal.Operation));
        }
        // Keep all evidence when a user resolves an interrupted save.
        return observed;
    }

    private async Task<Recovery> ObserveAsync(Journal journal, FileStream? active, CancellationToken token)
    {
        var parkedPath = ParkedPath(journal.Operation);
        var state = RecoveryState.Unverifiable;
        FileVersion? seen = null;
        if (active is not null)
        {
            seen = await ReadFileVersionAsync(active, token).ConfigureAwait(false);
            state = seen == journal.Result ? RecoveryState.Published
                : seen == journal.Original ? RecoveryState.ActivePreserved
                : RecoveryState.Conflict;
        }
        else
        {
            using var parked = TryOpen(parkedPath);
            if (parked is not null && await MatchesAsync(parked, journal.Original, token).ConfigureAwait(false))
                state = RecoveryState.CanRestore;
        }
        return new Recovery(state, listPath, parkedPath, CandidatePath(journal.Operation), JournalPath, journal, seen);
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
