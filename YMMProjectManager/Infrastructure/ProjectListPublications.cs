using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YMMProjectManager.Infrastructure;

internal sealed class ProjectListPublications(string directory)
{
    internal enum Outcome { Unknown, Unpublished, Committed }
    internal sealed record Receipt(int Version, Guid Id, Guid? DraftId, string? Expected, string Result, bool PublicationStarted);
    private sealed record Envelope(byte[] Payload, string Hash);
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    internal async Task WriteAsync(Receipt receipt, CancellationToken token)
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
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
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

    internal async Task<Outcome> ReconcileAsync(Guid draftId, string? current, Guid? currentOperation,
        Func<Guid, bool> hasUnconsumedCandidate, CancellationToken token)
    {
        ProjectListDrafts.CheckOrdinaryDirectory(directory);
        if (!Directory.Exists(directory)) return Outcome.Unknown;
        var known = false;
        var unpublished = true;
        var committed = false;
        foreach (var path in Directory.EnumerateFiles(directory, $"{Prefix(draftId)}*.publication.json"))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException("Invalid publication receipt path.");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            if (stream.Length > 4096) throw new InvalidDataException("Invalid publication receipt length.");
            var envelope = await JsonSerializer.DeserializeAsync<Envelope>(stream, Options, token).ConfigureAwait(false);
            if (envelope?.Payload is null || Digest(envelope.Payload) != envelope.Hash)
                throw new InvalidDataException("Invalid publication receipt checksum.");
            var receipt = JsonSerializer.Deserialize<Receipt>(envelope.Payload, Options);
            if (receipt is not { Version: 1 } || receipt.Id == Guid.Empty || receipt.DraftId != draftId ||
                Path.GetFileName(path) != Name(draftId, receipt.Id) ||
                !IsDigest(receipt.Result) || (receipt.Expected is not null && !IsDigest(receipt.Expected)))
                throw new InvalidDataException("Invalid publication receipt contents.");
            known = true;
            committed |= receipt.Result == current && receipt.Id == currentOperation;
            // Once publication starts, it consumes the candidate. Exact base bytes can also mean a later restore.
            unpublished &= receipt.Expected == current && (!receipt.PublicationStarted || hasUnconsumedCandidate(receipt.Id));
        }
        return committed ? Outcome.Committed : known && unpublished ? Outcome.Unpublished : Outcome.Unknown;
    }

    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static string Prefix(Guid? draftId) => draftId is { } id ? $"{id:N}-" : "standalone-";
    private static string Name(Guid? draftId, Guid id) => $"{Prefix(draftId)}{id:N}.publication.json";
}
