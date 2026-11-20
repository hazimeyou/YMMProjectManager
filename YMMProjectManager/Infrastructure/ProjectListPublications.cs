using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YMMProjectManager.Infrastructure;

internal sealed class ProjectListPublications(string directory)
{
    internal enum Outcome { Unknown, Unpublished, Committed }
    internal sealed record Receipt(int Version, Guid Id, Guid? DraftId, string? Expected, string Result,
        [property: JsonRequired] bool PublicationStarted,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ExpectedOperation = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProjectListReplacement.FileVersion? Candidate = null);
    private sealed record Envelope(byte[] Payload, string Hash);
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    internal async Task WriteAsync(Receipt receipt, CancellationToken token, Action? afterPartialWrite = null)
    {
        ProjectListDrafts.CheckOrdinaryDirectory(directory);
        Directory.CreateDirectory(directory);
        ProjectListDrafts.CheckOrdinaryDirectory(directory);
        var name = Name(receipt.DraftId, receipt.Id);
        var path = Path.Combine(directory, name);
        var temporary = Path.Combine(directory, $".{receipt.Id:N}.tmp");
        var payload = JsonSerializer.SerializeToUtf8Bytes(receipt, Options);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(payload, Digest(payload)), Options);
        var owned = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                owned = true;
                if (afterPartialWrite is null)
                    await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                else
                {
                    await stream.WriteAsync(bytes.AsMemory(0, bytes.Length / 2), token).ConfigureAwait(false);
                    afterPartialWrite();
                    await stream.WriteAsync(bytes.AsMemory(bytes.Length / 2), token).ConfigureAwait(false);
                }
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            if (receipt.PublicationStarted)
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path, overwrite: false);
            owned = false;
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

    internal async Task<Outcome> ReconcileAsync(Guid draftId, Guid? boundOperation, string? current, Guid? currentOperation,
        Func<Guid, ProjectListReplacement.FileVersion, CancellationToken, Task<bool>> verifyUnconsumedCandidate, CancellationToken token)
    {
        ProjectListDrafts.CheckOrdinaryDirectory(directory);
        if (!Directory.Exists(directory)) return Outcome.Unknown;
        var known = false;
        var unpublished = true;
        var committed = false;
        // Older claims did not name an operation. Keep their conservative all-receipts check.
        var pattern = boundOperation is { } operation ? Name(draftId, operation) : $"{Prefix(draftId)}*.publication.json";
        foreach (var path in Directory.EnumerateFiles(directory, pattern))
        {
            token.ThrowIfCancellationRequested();
            var receipt = await ReadAsync(path, token).ConfigureAwait(false);
            if (receipt.DraftId != draftId || (boundOperation is { } required && receipt.Id != required))
                throw new InvalidDataException("Invalid publication receipt binding.");
            known = true;
            committed |= receipt.Result == current && receipt.Id == currentOperation;
            if (boundOperation is not null)
            {
                if (committed || await IsPublishedAncestorAsync(receipt, current, currentOperation, token).ConfigureAwait(false))
                    return Outcome.Committed;
                if (!receipt.PublicationStarted) return Outcome.Unpublished;
            }
            // Publication consumes the staged file. Only its recorded identity and contents can prove that
            // it is still unconsumed: a later restore can also have the exact base bytes at the active name.
            unpublished = unpublished && receipt.Expected == current &&
                (!receipt.PublicationStarted || (receipt.Candidate is { } candidate &&
                    await verifyUnconsumedCandidate(receipt.Id, candidate, token).ConfigureAwait(false)));
        }
        return committed ? Outcome.Committed : known && unpublished ? Outcome.Unpublished : Outcome.Unknown;
    }

    // Only a chain ending in the actual current bytes can prove publication after a later save.
    // Missing, corrupt or duplicate links count as no proof.
    private async Task<bool> IsPublishedAncestorAsync(Receipt ancestor, string? currentHash, Guid? currentOperation, CancellationToken token)
    {
        if (!ancestor.PublicationStarted || currentHash is null) return false;
        var visited = new HashSet<Guid>();
        const int maxReceipts = 128;
        for (var i = 0; i < maxReceipts && currentOperation is { } operation && visited.Add(operation); i++)
        {
            token.ThrowIfCancellationRequested();
            if (operation == ancestor.Id) return currentHash == ancestor.Result;
            var candidates = Directory.EnumerateFiles(directory, $"*-{operation:N}.publication.json").Take(2).ToArray();
            if (candidates.Length != 1) return false;
            Receipt current;
            try { current = await ReadAsync(candidates[0], token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or FileNotFoundException) { return false; }
            if (current.Id != operation || !current.PublicationStarted || current.Result != currentHash || current.Expected is null)
                return false;
            currentHash = current.Expected;
            currentOperation = current.ExpectedOperation;
        }
        return false;
    }

    private static async Task<Receipt> ReadAsync(string path, CancellationToken token)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("Invalid publication receipt path.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (stream.Length > 4096) throw new InvalidDataException("Invalid publication receipt length.");
        var envelope = await JsonSerializer.DeserializeAsync<Envelope>(stream, Options, token).ConfigureAwait(false);
        if (envelope?.Payload is null || Digest(envelope.Payload) != envelope.Hash)
            throw new InvalidDataException("Invalid publication receipt checksum.");
        var receipt = JsonSerializer.Deserialize<Receipt>(envelope.Payload, Options);
        if (receipt is not { Version: 1 or 2 or 3 } || receipt.Id == Guid.Empty || receipt.DraftId == Guid.Empty ||
            receipt.ExpectedOperation == Guid.Empty || receipt.ExpectedOperation == receipt.Id ||
            (receipt.ExpectedOperation is not null && (receipt.Version == 1 || receipt.Expected is null)) ||
            Path.GetFileName(path) != Name(receipt.DraftId, receipt.Id) ||
            !IsDigest(receipt.Result) || (receipt.Expected is not null && !IsDigest(receipt.Expected)) ||
            (receipt.Candidate is not null) != (receipt.Version == 3 && receipt.PublicationStarted) ||
            (receipt.Candidate is { } candidate && (candidate.FileId == Guid.Empty || candidate.Sha256 != receipt.Result)))
            throw new InvalidDataException("Invalid publication receipt contents.");
        return receipt;
    }

    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static string Prefix(Guid? draftId) => draftId is { } id ? $"{id:N}-" : "standalone-";
    private static string Name(Guid? draftId, Guid id) => $"{Prefix(draftId)}{id:N}.publication.json";
}
