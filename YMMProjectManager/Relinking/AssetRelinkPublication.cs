using System.IO;
using System.Text.Json;
using YMMProjectManager.Infrastructure.Relink;

namespace YMMProjectManager.Infrastructure;

//CSJ,class=AssetRelinkRecovery,素材の再リンク復旧
internal static partial class AssetRelinkRecovery
{
    //CSJ,field=MaxPublicationBytes,最大確定反映バイト列
    private const int MaxPublicationBytes = 4096;

    //CSJ,record=OutputPublication,出力確定反映
    private sealed record OutputPublication(int Version, Guid OperationId, string OutputSha256, PhysicalFileId Identity);

    //CSJ,method=StagePreparedOutputAsync,準備済み出力を準備領域に置く（非同期）
    internal static async Task<(bool Success, string? ErrorMessage)> StagePreparedOutputAsync(
        //CSJ,parameter=evidence,証跡
        string evidence, RelinkDocumentContext context, RelinkSaveService saver, CancellationToken token)
    {
        //CSJ,variable=evidencePath,証跡パス
        var evidencePath = Path.GetFullPath(evidence);
        //CSJ,variable=stream,ストリーム
        await using var stream = OpenVerifiedPath(evidencePath);
        //CSJ,variable=intent,実行意図
        var intent = await ReadIntentAsync(stream.Stream, evidencePath, context.YmmpPath, token).ConfigureAwait(false);
        if (intent.Version < 4 || context.OriginalBytes is not { } original || original.Length > MaxSourceBytes ||
            Hash(original) != intent.SourceSha256)
            throw new InvalidDataException("The managed relink save does not match its prepared source.");

        //CSJ,variable=sourceLease,元リース
        await using var sourceLease = OpenVerifiedPath(intent.Source);
        var source = sourceLease.Stream;
        if (GetPhysicalId(source) != intent.SourceIdentity || source.Length != original.Length ||
            await AssetContentHash.ReadSha256Async(source, token).ConfigureAwait(false) != intent.SourceSha256)
            throw new InvalidDataException("The source project changed after relink preparation.");

        //CSJ,variable=candidates,候補一覧
        var candidates = new List<VerifiedFileLease>();
        try
        {
            //CSJ,variable=comparison,比較
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

    //CSJ,method=OpenOutputAsync,出力を開く（非同期）
    private static async Task<(VerifiedFileLease Lease, bool NeedsPublication)> OpenOutputAsync(Intent intent, string evidencePath,
        //CSJ,parameter=resume,再開
        bool resume, CancellationToken token)
    {
        if (intent.Version < 4) return (OpenVerifiedPath(intent.Output), false);
        return await OpenProvenOutputAsync(evidencePath, intent.Output, intent.OperationId, intent.OutputSha256,
            resume, token).ConfigureAwait(false);
    }

    //CSJ,method=StageOwnedOutputAsync,管理対象の出力を準備領域に置く（非同期）
    private static Task<(bool Success, string? ErrorMessage)> StageOwnedOutputAsync(string evidencePath,
        RelinkDocumentContext context, string destination, Guid operationId, string outputSha256, RelinkSaveService saver,
        CancellationToken token) =>
        saver.StagePreparedAsync(context, destination, StagedOutputPath(evidencePath),
            async (staged, stagedToken) =>
            {
                if (await AssetContentHash.ReadSha256Async(staged, stagedToken).ConfigureAwait(false) != outputSha256)
                    throw new InvalidDataException("The staged relink project differs from its prepared bytes.");
                //CSJ,variable=publication,確定反映
                var publication = new OutputPublication(1, operationId, outputSha256, GetPhysicalId(staged));
                await WriteEvidenceAsync(PublicationPath(evidencePath), publication, MaxPublicationBytes, stagedToken).ConfigureAwait(false);
            }, token);

    private static async Task<(VerifiedFileLease Lease, bool NeedsPublication)> OpenProvenOutputAsync(string evidencePath, string destination,
        //CSJ,parameter=operationId,操作ID
        Guid operationId, string outputSha256, bool resume, CancellationToken token)
    {
        //CSJ,variable=proofLease,確認証跡リース
        await using var proofLease = OpenVerifiedPath(PublicationPath(evidencePath));
        //CSJ,variable=proof,確認証跡
        var proof = proofLease.Stream;
        if (proof.Length > MaxPublicationBytes) throw new InvalidDataException("The relink publication record is too large.");
        //CSJ,variable=publication,確定反映
        var publication = await JsonSerializer.DeserializeAsync<OutputPublication>(proof, JsonOptions, token).ConfigureAwait(false);
        if (publication is not { Version: 1 } || publication.OperationId != operationId ||
            publication.OutputSha256 != outputSha256 || publication.Identity is null || publication.Identity.FileId == Guid.Empty)
            throw new InvalidDataException("The relink publication record does not match the managed output.");

        //CSJ,variable=pending,保留
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

    //CSJ,method=StagedOutputPath,準備済み出力パス
    private static string StagedOutputPath(string evidencePath) => evidencePath + ".output.tmp";

    //CSJ,method=PublicationPath,確定反映パス
    private static string PublicationPath(string evidencePath) => evidencePath + ".publish";
}
