using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using YMMProjectManager.Application;

namespace YMMProjectManager.Infrastructure;

internal sealed class ProjectListDrafts(string directory)
{
    internal sealed record Evidence(Guid Id, string Hash);
    private sealed record Document(int Version, Guid Id, string? BaseRevision, byte[] Baseline, byte[] Local,
        Dictionary<string, Evidence> Additions, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? PredecessorId);
    private readonly record struct DraftIdentity(Guid Id, Guid? Predecessor, Guid? Operation, string ReadyPath);
    private sealed record Envelope(byte[] Payload, string Sha256);
    internal sealed record Draft(ProjectStore Baseline, ProjectStore Local, Dictionary<string, Evidence> Additions);
    private const int MaxBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    internal string DirectoryPath => directory;

    internal Guid GetReadyId(string path) => RequireCurrent(path).Id;

    internal (Guid Id, Guid? Operation) GetClaimedIdentity(string path)
    {
        var identity = CheckPath(path, claimed: true);
        return (identity.Id, identity.Operation);
    }

    internal string GetReadyPath(string claimedPath) => CheckPath(claimedPath, claimed: true, requireFile: false).ReadyPath;

    internal async Task<string> WriteAsync(ProjectStore baseline, ProjectStore local, Dictionary<string, Evidence> evidence,
        string? predecessor = null, Action? beforePublication = null, Action? afterPublication = null)
    {
        if (baseline.Revision is null) throw new InvalidDataException("Recovery draft has no base revision.");
        Guid? previous = null;
        if (predecessor is not null)
        {
            previous = RequireCurrent(predecessor).Id;
            await ReadAsync(predecessor).ConfigureAwait(false);
        }
        var id = Guid.NewGuid();
        var document = new Document(previous.HasValue ? 2 : 1, id, baseline.Revision.Sha256, JsonProjectRepository.SerializeStore(baseline),
            JsonProjectRepository.SerializeStore(local), evidence, previous);
        var payload = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(payload, Convert.ToHexString(SHA256.HashData(payload))), Options);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Recovery draft is too large.");
        CheckDirectory();
        Directory.CreateDirectory(directory);
        CheckDirectory();
        var name = previous is Guid predecessorId ? $"{id:N}.{predecessorId:N}" : $"{id:N}";
        var path = Path.Combine(directory, $"{name}.draft.json");
        var temporary = path + ".tmp";
        var owned = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                owned = true;
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            beforePublication?.Invoke();
            File.Move(temporary, path, overwrite: false);
            owned = false;
            afterPublication?.Invoke();
            return path;
        }
        finally
        {
            if (owned)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal Task<Draft> ReadAsync(string path, bool claimed = false) =>
        ReadCoreAsync(path, RequireCurrent(path, claimed));

    internal async Task<(Draft Draft, bool ExportOnly)> ReadForReviewAsync(string path, bool claimed = false)
    {
        var identity = CheckPath(path, claimed);
        return (await ReadCoreAsync(path, identity).ConfigureAwait(false), !CanReplay(identity, path));
    }

    internal Task<Draft> ReadArchivedAsync(string path) => ReadCoreAsync(path, CheckPath(path, archived: true));

    private static async Task<Draft> ReadCoreAsync(string path, DraftIdentity identity)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (stream.Length > MaxBytes) throw new InvalidDataException("Recovery draft is too large.");
        var envelope = await JsonSerializer.DeserializeAsync<Envelope>(stream, Options).ConfigureAwait(false);
        if (envelope?.Payload is null || envelope.Sha256 != Convert.ToHexString(SHA256.HashData(envelope.Payload)))
            throw new InvalidDataException("Recovery draft checksum is invalid.");
        var document = JsonSerializer.Deserialize<Document>(envelope.Payload, Options);
        var expectedVersion = identity.Predecessor.HasValue ? 2 : 1;
        if (document is null || document.Version != expectedVersion || document.Id != identity.Id ||
            document.PredecessorId != identity.Predecessor || document.Baseline is null || document.Local is null ||
            document.Additions is null || (document.BaseRevision is not null && !IsHash(document.BaseRevision)))
            throw new InvalidDataException("Recovery draft format or identity is invalid.");
        var baseline = JsonProjectRepository.DeserializeStore(document.Baseline);
        var local = JsonProjectRepository.DeserializeStore(document.Local);
        if (!baseline.IsRecoverySafe || !local.IsRecoverySafe ||
            (document.BaseRevision is null && (baseline.Projects.Count != 0 || baseline.Folders.Count != 0)) ||
            document.Additions.Any(pair => pair.Value is null || pair.Value.Id == Guid.Empty || !IsHash(pair.Value.Hash)))
            throw new InvalidDataException("Recovery draft contains unsupported data.");
        baseline.Revision = baseline.Revision! with { Sha256 = document.BaseRevision };
        local.Revision = local.Revision! with { Sha256 = document.BaseRevision };
        return new Draft(baseline, local, document.Additions);
    }

    internal string Claim(string path, Guid operation)
    {
        if (operation == Guid.Empty) throw new ArgumentException("A recovery claim requires an operation ID.", nameof(operation));
        RequireCurrent(path);
        var claimed = $"{path}.{operation:N}.saving";
        File.Move(path, claimed, overwrite: false);
        return claimed;
    }

    internal string ReleaseUnpublishedClaim(string path)
    {
        RequireCurrent(path, claimed: true);
        var ready = GetReadyPath(path);
        File.Move(path, ready, overwrite: false);
        return ready;
    }

    internal void Retire(string path, bool claimed = false)
    {
        CheckPath(path, claimed);
        var closed = claimed ? GetReadyPath(path) + ".closed" : path + ".closed";
        File.Move(path, closed, overwrite: false);
    }

    private DraftIdentity RequireCurrent(string path, bool claimed = false)
    {
        var identity = CheckPath(path, claimed);
        if (!CanReplay(identity, path))
            throw new InvalidDataException("This recovery generation is superseded or ambiguous. Export it instead of replaying it.");
        return identity;
    }

    private bool CanReplay(DraftIdentity identity, string path)
    {
        var full = Path.GetFullPath(path);
        // The published name fences a predecessor even when the successor payload is unreadable.
        // A temporary file exists before the generation changes and does not establish that fence.
        if (PublishedNames($"*.{identity.Id:N}.draft.json*").Any()) return false;
        var peers = identity.Predecessor is Guid predecessor
            ? PublishedNames($"*.{predecessor:N}.draft.json*")
            : PublishedNames(Path.GetFileName(identity.ReadyPath) + "*");
        return !peers.Any(peer => !string.Equals(peer, full, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<string> PublishedNames(string pattern) =>
        Directory.EnumerateFileSystemEntries(directory, pattern)
            .Where(path => !Path.GetFileName(path).EndsWith(".tmp", StringComparison.Ordinal));

    private DraftIdentity CheckPath(string path, bool claimed = false, bool requireFile = true, bool archived = false)
    {
        CheckDirectory();
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full);
        Guid? operation = null;
        if (archived)
        {
            const string suffix = ".closed";
            if (!name.EndsWith(suffix, StringComparison.Ordinal)) throw new InvalidDataException("Recovery archive path is invalid.");
            name = name[..^suffix.Length];
        }
        if (claimed)
        {
            if (!name.EndsWith(".saving", StringComparison.Ordinal)) throw new InvalidDataException("Recovery claim is invalid.");
            name = name[..^7];
            var separator = name.LastIndexOf('.');
            if (!name.EndsWith(".draft.json", StringComparison.Ordinal) && separator >= 0 &&
                Guid.TryParseExact(name[(separator + 1)..], "N", out var parsed) && parsed != Guid.Empty)
            {
                operation = parsed;
                name = name[..separator];
            }
        }
        const string extension = ".draft.json";
        if (!string.Equals(Path.GetDirectoryName(full), directory, StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(extension, StringComparison.Ordinal) ||
            (requireFile && (File.GetAttributes(full) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0))
            throw new InvalidDataException("Recovery draft path is invalid.");
        var stem = name.AsSpan(0, name.Length - extension.Length);
        Guid? predecessor = null;
        if (stem.Length == 65 && stem[32] == '.')
        {
            if (!Guid.TryParseExact(stem[33..], "N", out var previous) || previous == Guid.Empty)
                throw new InvalidDataException("Recovery draft predecessor is invalid.");
            predecessor = previous;
            stem = stem[..32];
        }
        if (!Guid.TryParseExact(stem, "N", out var id) || id == Guid.Empty || id == predecessor)
            throw new InvalidDataException("Recovery draft identity is invalid.");
        return new DraftIdentity(id, predecessor, operation, Path.Combine(directory, name));
    }

    private void CheckDirectory() => CheckOrdinaryDirectory(directory);

    internal static void CheckOrdinaryDirectory(string directory)
    {
        for (var item = new DirectoryInfo(directory); item is not null; item = item.Parent)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(item.FullName); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Recovery directory traverses a reparse point.");
        }
    }

    private static bool IsHash(string? text) => text is { Length: 64 } && text.All(Uri.IsHexDigit);
}
