using System.IO;
using System.Text.Json;
using YMMProjectManager.Infrastructure.Relink;

namespace YMMProjectManager.Infrastructure;

internal static partial class AssetRelinkRecovery
{
    private const int MaxPublicationBytes = 4096;

    private sealed record OutputPublication(int Version, Guid OperationId, string OutputSha256, PhysicalFileId Identity);

    internal static async Task<(bool Success, string? ErrorMessage)> StagePreparedOutputAsync(
        string evidence, RelinkDocumentContext context, RelinkSaveService saver, CancellationToken token)
    {
        var evidencePath = Path.GetFullPath(evidence);
        await using var stream = OpenVerifiedPath(evidencePath);
        var intent = await ReadIntentAsync(stream.Stream, evidencePath, context.YmmpPath, token).ConfigureAwait(false);
        if (intent.Version < 4 || context.OriginalBytes is not { } original || original.Length > MaxSourceBytes ||
            Hash(original) != intent.SourceSha256)
            throw new InvalidDataException("The managed relink save does not match its prepared source.");

        await using var sourceLease = OpenVerifiedPath(intent.Source);
        var source = sourceLease.Stream;
        if (GetPhysicalId(source) != intent.SourceIdentity || source.Length != original.Length ||
            await AssetContentHash.ReadSha256Async(source, token).ConfigureAwait(false) != intent.SourceSha256)
            throw new InvalidDataException("The source project changed after relink preparation.");

        var candidates = new List<VerifiedFileLease>();
        try
        {
            foreach (var comparison in intent.Approved.DistinctBy(item => item.CandidatePath, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var candidate = OpenVerifiedPath(comparison.CandidatePath);
                candidates.Add(candidate);
                if (await AssetContentHash.ReadSha256Async(candidate.Stream, token).ConfigureAwait(false) != comparison.CandidateSha256)
                    throw new InvalidDataException("A candidate changed after relink preparation.");
            }
            return await StageOwnedOutputAsync(evidencePath, context, intent.Output, intent.OperationId, intent.OutputSha256,
                saver, token).ConfigureAwait(false);
        }
        finally
        {
            foreach (var candidate in candidates) await candidate.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<(VerifiedFileLease Lease, bool NeedsPublication)> OpenOutputAsync(Intent intent, string evidencePath,
        bool resume, CancellationToken token)
    {
        if (intent.Version < 4) return (OpenVerifiedPath(intent.Output), false);
        return await OpenProvenOutputAsync(evidencePath, intent.Output, intent.OperationId, intent.OutputSha256,
            resume, token).ConfigureAwait(false);
    }

    private static Task<(bool Success, string? ErrorMessage)> StageOwnedOutputAsync(string evidencePath,
        RelinkDocumentContext context, string destination, Guid operationId, string outputSha256, RelinkSaveService saver,
        CancellationToken token) =>
        saver.StagePreparedAsync(context, destination, StagedOutputPath(evidencePath),
            async (staged, stagedToken) =>
            {
                if (await AssetContentHash.ReadSha256Async(staged, stagedToken).ConfigureAwait(false) != outputSha256)
                    throw new InvalidDataException("The staged relink project differs from its prepared bytes.");
                var publication = new OutputPublication(1, operationId, outputSha256, GetPhysicalId(staged));
                await WriteEvidenceAsync(PublicationPath(evidencePath), publication, MaxPublicationBytes, stagedToken).ConfigureAwait(false);
            }, token);

    private static async Task<(VerifiedFileLease Lease, bool NeedsPublication)> OpenProvenOutputAsync(string evidencePath, string destination,
        Guid operationId, string outputSha256, bool resume, CancellationToken token)
    {
        await using var proofLease = OpenVerifiedPath(PublicationPath(evidencePath));
        var proof = proofLease.Stream;
        if (proof.Length > MaxPublicationBytes) throw new InvalidDataException("The relink publication record is too large.");
        var publication = await JsonSerializer.DeserializeAsync<OutputPublication>(proof, JsonOptions, token).ConfigureAwait(false);
        if (publication is not { Version: 1 } || publication.OperationId != operationId ||
            publication.OutputSha256 != outputSha256 || publication.Identity is null || publication.Identity.FileId == Guid.Empty)
            throw new InvalidDataException("The relink publication record does not match the managed output.");

        var pending = false;
        VerifiedFileLease output;
        try { output = OpenVerifiedPath(destination); }
        catch (FileNotFoundException) when (resume)
        {
            output = OpenVerifiedPath(StagedOutputPath(evidencePath), allowRename: true);
            pending = true;
        }
        try
        {
            if (GetPhysicalId(output.Stream) != publication.Identity)
                throw new InvalidDataException("The relink output was replaced or does not match the prepared publication.");
            return (output, pending);
        }
        catch
        {
            await output.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string StagedOutputPath(string evidencePath) => evidencePath + ".output.tmp";

    private static string PublicationPath(string evidencePath) => evidencePath + ".publish";
}
