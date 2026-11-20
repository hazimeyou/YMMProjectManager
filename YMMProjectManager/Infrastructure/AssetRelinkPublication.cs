using System.IO;
using System.Text.Json;
using YMMProjectManager.Infrastructure.Relink;

namespace YMMProjectManager.Infrastructure;

internal static partial class AssetRelinkRecovery
{
    private const int MaxPublicationBytes = 4096;

    private sealed record OutputPublication(int Version, Guid OperationId, string OutputSha256, PhysicalFileId Identity);

    internal static async Task<(bool Success, string? ErrorMessage)> SavePreparedOutputAsync(
        string evidence, RelinkDocumentContext context, RelinkSaveService saver, CancellationToken token)
    {
        var evidencePath = Path.GetFullPath(evidence);
        await using var stream = OpenVerifiedPath(evidencePath);
        var intent = await ReadIntentAsync(stream, evidencePath, context.YmmpPath, token).ConfigureAwait(false);
        if (intent.Version != 4 || context.OriginalBytes is not { } original || original.Length > MaxSourceBytes ||
            Hash(original) != intent.SourceSha256)
            throw new InvalidDataException("The managed relink save does not match its prepared source.");

        using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(intent.Source)!);
        await using var source = OpenVerifiedPath(intent.Source);
        if (GetPhysicalId(source) != intent.SourceIdentity || source.Length != original.Length ||
            await HashAsync(source, token).ConfigureAwait(false) != intent.SourceSha256)
            throw new InvalidDataException("The source project changed after relink preparation.");

        var candidates = new List<FileStream>();
        try
        {
            foreach (var comparison in intent.Approved.DistinctBy(item => item.CandidatePath, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var candidate = OpenVerifiedPath(comparison.CandidatePath);
                candidates.Add(candidate);
                if (await HashAsync(candidate, token).ConfigureAwait(false) != comparison.CandidateSha256)
                    throw new InvalidDataException("A candidate changed after relink preparation.");
            }
            return await saver.SavePreparedAsync(context, intent.Output, StagedOutputPath(evidencePath),
                async (staged, stagedToken) =>
                {
                    if (await HashAsync(staged, stagedToken).ConfigureAwait(false) != intent.OutputSha256)
                        throw new InvalidDataException("The staged relink project differs from its prepared bytes.");
                    var publication = new OutputPublication(1, intent.OperationId, intent.OutputSha256, GetPhysicalId(staged));
                    await WriteEvidenceAsync(PublicationPath(evidencePath), publication, MaxPublicationBytes, stagedToken).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
        }
        finally
        {
            foreach (var candidate in candidates) await candidate.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<(FileStream Stream, bool NeedsPublication)> OpenOutputAsync(Intent intent, string evidencePath,
        bool resume, CancellationToken token)
    {
        if (intent.Version != 4) return (OpenVerifiedPath(intent.Output), false);

        await using var proof = OpenVerifiedPath(PublicationPath(evidencePath));
        if (proof.Length > MaxPublicationBytes) throw new InvalidDataException("The relink publication record is too large.");
        var publication = await JsonSerializer.DeserializeAsync<OutputPublication>(proof, JsonOptions, token).ConfigureAwait(false);
        if (publication is not { Version: 1 } || publication.OperationId != intent.OperationId ||
            publication.OutputSha256 != intent.OutputSha256 || publication.Identity is null || publication.Identity.FileId == Guid.Empty)
            throw new InvalidDataException("The relink publication record does not match the managed output.");

        var pending = false;
        FileStream output;
        try { output = OpenVerifiedPath(intent.Output); }
        catch (FileNotFoundException) when (resume)
        {
            var handle = OwnedFilePublication.OpenOrdinaryPath(StagedOutputPath(evidencePath), expectDirectory: false, allowRename: true);
            try { output = new FileStream(handle, FileAccess.Read); }
            catch { handle.Dispose(); throw; }
            pending = true;
        }
        try
        {
            if (GetPhysicalId(output) != publication.Identity)
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
