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
        Dictionary<string, Evidence> Additions);
    private sealed record Envelope(byte[] Payload, string Sha256);
    internal sealed record Draft(ProjectStore Baseline, ProjectStore Local, Dictionary<string, Evidence> Additions);
    private const int MaxBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    internal string DirectoryPath => directory;

    internal Guid GetClaimedId(string path) => CheckPath(path, claimed: true);

    internal async Task<string> WriteAsync(ProjectStore baseline, ProjectStore local, Dictionary<string, Evidence> evidence)
    {
        if (baseline.Revision is null) throw new InvalidDataException("Recovery draft has no base revision.");
        var id = Guid.NewGuid();
        var document = new Document(1, id, baseline.Revision.Sha256, JsonProjectRepository.SerializeStore(baseline),
            JsonProjectRepository.SerializeStore(local), evidence);
        var payload = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(payload, Convert.ToHexString(SHA256.HashData(payload))), Options);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Recovery draft is too large.");
        CheckDirectory();
        Directory.CreateDirectory(directory);
        CheckDirectory();
        var path = Path.Combine(directory, $"{id:N}.draft.json");
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
            File.Move(temporary, path);
            owned = false;
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

    internal async Task<Draft> ReadAsync(string path, bool claimed = false)
    {
        var id = CheckPath(path, claimed);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (stream.Length > MaxBytes) throw new InvalidDataException("Recovery draft is too large.");
        var envelope = await JsonSerializer.DeserializeAsync<Envelope>(stream, Options).ConfigureAwait(false);
        if (envelope?.Payload is null || envelope.Sha256 != Convert.ToHexString(SHA256.HashData(envelope.Payload)))
            throw new InvalidDataException("Recovery draft checksum is invalid.");
        var document = JsonSerializer.Deserialize<Document>(envelope.Payload, Options);
        if (document is not { Version: 1 } || document.Id != id || document.Baseline is null || document.Local is null ||
            document.Additions is null || (document.BaseRevision is not null && !IsHash(document.BaseRevision)))
            throw new InvalidDataException("Recovery draft format or identity is invalid.");
        var baseline = JsonProjectRepository.DeserializeStore(document.Baseline);
        var local = JsonProjectRepository.DeserializeStore(document.Local);
        if (!baseline.IsRecoverySafe || !local.IsRecoverySafe ||
            (document.BaseRevision is null && (baseline.Projects.Count != 0 || baseline.Folders.Count != 0)) ||
            document.Additions.Any(pair => pair.Value is null || pair.Value.Id == Guid.Empty || !IsHash(pair.Value.Hash)))
            throw new InvalidDataException("Recovery draft contains unsupported data.");
        baseline.Revision = new ProjectListRevision(document.BaseRevision);
        local.Revision = baseline.Revision;
        return new Draft(baseline, local, document.Additions);
    }

    internal string Claim(string path)
    {
        CheckPath(path);
        var claimed = path + ".saving";
        File.Move(path, claimed, overwrite: false);
        return claimed;
    }

    internal string ReleaseUnpublishedClaim(string path)
    {
        CheckPath(path, claimed: true);
        var ready = path[..^7];
        File.Move(path, ready, overwrite: false);
        return ready;
    }

    internal void Retire(string path, bool claimed = false)
    {
        CheckPath(path, claimed);
        var closed = claimed ? path[..^7] + ".closed" : path + ".closed";
        File.Move(path, closed, overwrite: false);
    }

    private Guid CheckPath(string path, bool claimed = false)
    {
        CheckDirectory();
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full);
        if (claimed)
        {
            if (!name.EndsWith(".saving", StringComparison.Ordinal)) throw new InvalidDataException("Recovery claim is invalid.");
            name = name[..^7];
        }
        if (!string.Equals(Path.GetDirectoryName(full), directory, StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(".draft.json", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[..^11], "N", out var id) ||
            (File.GetAttributes(full) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("Recovery draft path is invalid.");
        return id;
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
