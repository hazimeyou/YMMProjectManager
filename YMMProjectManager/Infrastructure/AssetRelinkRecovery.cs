using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YMMProjectManager.Infrastructure.Relink;

namespace YMMProjectManager.Infrastructure;

/// <summary>Durable intent for an output that is published separately from its metadata revision.</summary>
internal static partial class AssetRelinkRecovery
{
    private const int MaxIntentBytes = 8 * 1024 * 1024;
    private const int MaxSourceBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    internal sealed record Comparison(int RowIndex, string OriginalPath, string CandidatePath, string? ExpectedSha256, string CandidateSha256,
        string TypeHint)
    {
        public bool RequiresConfirmation => !string.Equals(ExpectedSha256, CandidateSha256, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record PhysicalFileId(ulong Volume, Guid FileId);
    internal sealed record StrictAssetProof(int RowIndex, string Name, string? ManifestPath, string? ManifestSha256);

    internal sealed record Intent(int Version, Guid OperationId, string Source, string SourceSha256, string Output, string OutputSha256,
        AssetRevisionSeries.Head Root, AssetRevisionSeries.Head Parent, AssetManifest Next, Comparison[] Approved,
        AssetRevisionSeries.Head[]? ObservedTips = null, PhysicalFileId? SourceIdentity = null, StrictAssetProof[]? StrictAssets = null);

    internal sealed record OutputReceipt(AssetRevisionSeries.Head Revision, Guid OperationId, string Source, string SourceSha256,
        string Output, string OutputSha256, PhysicalFileId SourceIdentity, PhysicalFileId OutputIdentity);

    internal static Task<IReadOnlyList<Comparison>> ReviewAsync(RelinkDocumentContext context, string seriesDirectory,
        AssetRevisionSeries.Head root, AssetRevisionSeries.Head parent, CancellationToken token)
        => ReviewAsync(context, seriesDirectory, root, parent, [parent], token);

    internal static async Task<IReadOnlyList<Comparison>> ReviewAsync(RelinkDocumentContext context, string seriesDirectory,
        AssetRevisionSeries.Head root, AssetRevisionSeries.Head parent, IReadOnlyList<AssetRevisionSeries.Head> observedTips,
        CancellationToken token, bool requireStrict = false)
    {
        var selected = await ResolveTipAsync(seriesDirectory, root, parent, observedTips, token).ConfigureAwait(false);
        var rows = RelinkCandidateVerifier.SnapshotRows(context.Rows.Where(row => row.Status == RelinkStatus.Updated));
        var strict = requireStrict ? new StrictRelinkAssets(context.YmmpPath, context.OriginalJsonText, selected) : null;
        var (review, _) = await InspectRowsAsync(Path.GetDirectoryName(Path.GetFullPath(context.YmmpPath))!, selected, rows, token,
            strict: strict).ConfigureAwait(false);
        return review;
    }

    internal static Task<string> PrepareAsync(RelinkDocumentContext context, string destination, string seriesDirectory,
        AssetRevisionSeries.Head root, AssetRevisionSeries.Head parent, IReadOnlyList<Comparison> reviewed, bool confirmedMismatches, CancellationToken token)
        => PrepareAsync(context, destination, seriesDirectory, root, parent, [parent], reviewed, confirmedMismatches, token);

    internal static Task<string> PrepareAsync(RelinkDocumentContext context, string destination, string seriesDirectory,
        AssetRevisionSeries.Head root, AssetRevisionSeries.Head parent, IReadOnlyList<AssetRevisionSeries.Head> observedTips,
        IReadOnlyList<Comparison> reviewed, bool confirmedMismatches, CancellationToken token)
        => PrepareCoreAsync(context, destination, seriesDirectory, root, parent, observedTips, reviewed, confirmedMismatches, 3, token);

    internal static Task<string> PrepareManagedOutputAsync(RelinkDocumentContext context, string destination, string seriesDirectory,
        AssetRevisionSeries.Head root, AssetRevisionSeries.Head parent, IReadOnlyList<AssetRevisionSeries.Head> observedTips,
        IReadOnlyList<Comparison> reviewed, bool confirmedMismatches, CancellationToken token)
        => PrepareCoreAsync(context, destination, seriesDirectory, root, parent, observedTips, reviewed, confirmedMismatches, 4, token);

    internal static Task<string> PrepareStrictManagedOutputAsync(RelinkDocumentContext context, string destination, string seriesDirectory,
        AssetRevisionSeries.Head root, AssetRevisionSeries.Head parent, IReadOnlyList<AssetRevisionSeries.Head> observedTips,
        IReadOnlyList<Comparison> reviewed, StrictAssetProof[] proofs, CancellationToken token) =>
        PrepareCoreAsync(context, destination, seriesDirectory, root, parent, observedTips, reviewed, false, 5, token, proofs);

    private static async Task<string> PrepareCoreAsync(RelinkDocumentContext context, string destination, string seriesDirectory,
        AssetRevisionSeries.Head root, AssetRevisionSeries.Head parent, IReadOnlyList<AssetRevisionSeries.Head> observedTips,
        IReadOnlyList<Comparison> reviewed, bool confirmedMismatches, int version, CancellationToken token,
        StrictAssetProof[]? proofs = null)
    {
        token.ThrowIfCancellationRequested();
        var expectedTips = observedTips.ToArray();
        var source = Path.GetFullPath(context.YmmpPath);
        var output = Path.GetFullPath(destination);
        ValidatePaths(source, output);
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("The relink output already exists.");
        var original = context.OriginalBytes?.ToArray() ?? throw new InvalidDataException("The source snapshot is missing.");
        if (original.Length > MaxSourceBytes) throw new InvalidDataException("The project is too large for relink recovery.");
        var rows = RelinkCandidateVerifier.SnapshotRows(context.Rows.Where(row => row.Status == RelinkStatus.Updated));
        var selected = await ResolveTipAsync(seriesDirectory, root, parent, expectedTips, token).ConfigureAwait(false);
        var sourceDirectory = Path.GetDirectoryName(source)!;
        var strict = proofs is null ? null : new StrictRelinkAssets(source, context.OriginalJsonText, selected);
        var (review, candidates) = await InspectRowsAsync(sourceDirectory, selected, rows, token, strict: strict).ConfigureAwait(false);
        if (!review.SequenceEqual(reviewed)) throw new InvalidDataException("A relink candidate changed after review.");
        if (review.Any(item => item.RequiresConfirmation) && !confirmedMismatches)
            throw new InvalidDataException("Missing or different asset hashes require confirmation.");

        var next = AssetManifestStore.Create(output, candidates, DateTimeOffset.UtcNow,
            selected, confirmedProjectAssociation: true, confirmedHashChanges: confirmedMismatches, projectId: root.ProjectId);
        await using var sourceLease = OpenVerifiedPath(source);
        var sourceHash = Hash(original);
        if (await HashAsync(sourceLease.Stream, token).ConfigureAwait(false) != sourceHash)
            throw new InvalidDataException("The source project changed before relink preparation.");
        var intent = new Intent(version, next.RevisionId, source, sourceHash, output, Hash(RelinkJsonUpdate.Apply(original, rows)),
            root, parent, next, review, expectedTips, GetPhysicalId(sourceLease.Stream), proofs);
        if (proofs is not null) ValidateStrictProofs(intent);
        var evidence = EvidencePath(source, intent.OperationId);
        await WriteEvidenceAsync(evidence, intent, MaxIntentBytes, token).ConfigureAwait(false);
        return evidence;
    }

    private static async Task<AssetManifest> ResolveTipAsync(string directory, AssetRevisionSeries.Head root,
        AssetRevisionSeries.Head parent, IReadOnlyList<AssetRevisionSeries.Head> observedTips, CancellationToken token)
    {
        var history = await AssetRevisionSeries.ResolveBranchAsync(directory, root.ProjectId, root.RevisionId, root.Sha256,
            parent.RevisionId, parent.Sha256, token).ConfigureAwait(false);
        if (!history.Graph.Heads.Contains(parent) || !AssetRevisionSeries.SameTips(history.Graph.Heads, observedTips))
            throw new InvalidDataException("The metadata branches changed or the selected revision is no longer a tip.");
        return history.Selected.Manifest;
    }

    private static async Task<(Comparison[], AssetMetadata[])> InspectRowsAsync(string sourceDirectory, AssetManifest manifest,
        List<RelinkRow> rows, CancellationToken token, List<VerifiedFileLease>? heldCandidates = null, StrictRelinkAssets? strict = null)
    {
        if (rows.Count is 0 or > 10) throw new InvalidDataException("A managed relink must have between one and ten candidate rows.");
        var expectedPaths = manifest.Assets.ToDictionary(
            item => Path.GetFullPath(item.RelativePath, sourceDirectory), StringComparer.OrdinalIgnoreCase);
        var candidates = new Dictionary<string, AssetMetadata>(StringComparer.OrdinalIgnoreCase);
        var review = new List<Comparison>(rows.Count);
        foreach (var row in rows)
        {
            if (row.SelectedCandidate is null || !Path.IsPathFullyQualified(row.SelectedCandidate))
                throw new InvalidDataException("An absolute relink candidate path is required.");
            RelinkJsonUpdate.EnsureSupported(row);
            var candidate = Path.GetFullPath(row.SelectedCandidate);
            var relative = Path.GetRelativePath(sourceDirectory, candidate);
            if (!candidates.TryGetValue(candidate, out var actual))
            {
                expectedPaths.TryGetValue(candidate, out var prior);
                if (heldCandidates is null)
                    actual = await AssetInventory.InspectAsync(sourceDirectory, relative, prior?.DownloadUrl, token).ConfigureAwait(false);
                else
                {
                    var path = AssetInventory.ResolveRelativePath(sourceDirectory, relative);
                    var lease = OpenVerifiedPath(path);
                    heldCandidates.Add(lease);
                    actual = new AssetMetadata(relative, lease.Stream.Length, await HashAsync(lease.Stream, token).ConfigureAwait(false), prior?.DownloadUrl);
                }
                candidates.Add(candidate, actual);
            }
            AssetMetadata? expected;
            if (strict is null) expectedPaths.TryGetValue(Path.GetFullPath(row.OriginalPath, sourceDirectory), out expected);
            else expected = strict.FindExpected(row.OriginalPath);
            review.Add(new Comparison(row.RowIndex, row.OriginalPath, row.SelectedCandidate, expected?.Sha256, actual.Sha256, row.TypeHint));
        }
        return (review.ToArray(), candidates.Values.ToArray());
    }

    internal static async Task<AssetRevisionSeries.Head> CompleteAsync(string evidence, string seriesDirectory, AssetRevisionSeries.Head associatedRoot,
        CancellationToken token, string? expectedSource = null)
        => (await CompleteForTransferAsync(evidence, seriesDirectory, associatedRoot, token, expectedSource).ConfigureAwait(false)).Revision;

    internal static async Task<OutputReceipt> CompleteForTransferAsync(string evidence, string seriesDirectory, AssetRevisionSeries.Head associatedRoot,
        CancellationToken token, string? expectedSource = null, bool resumePreparedOutput = false, bool requireStrictPrepared = false)
    {
        var evidencePath = Path.GetFullPath(evidence);
        await using var intentStream = OpenVerifiedPath(evidencePath);
        var intent = await ReadIntentAsync(intentStream.Stream, evidencePath, expectedSource, token).ConfigureAwait(false);
        if (intent.Root != associatedRoot)
            throw new InvalidDataException("The relink recovery record belongs to a different project.");

        var history = await AssetRevisionSeries.ResolveBranchAsync(seriesDirectory, associatedRoot.ProjectId, associatedRoot.RevisionId,
            associatedRoot.Sha256, intent.Parent.RevisionId, intent.Parent.Sha256, token).ConfigureAwait(false);
        await using var sourceLease = OpenVerifiedPath(intent.Source);
        var source = sourceLease.Stream;
        var sourceIdentity = GetPhysicalId(source);
        if (intent.SourceIdentity is not null && sourceIdentity != intent.SourceIdentity)
            throw new InvalidDataException("The source project was replaced after relink preparation.");
        if (source.Length > MaxSourceBytes) throw new InvalidDataException("The project is too large for relink recovery.");
        var original = new byte[checked((int)source.Length)];
        await source.ReadExactlyAsync(original, token).ConfigureAwait(false);
        var rows = intent.Approved.Select(item => new RelinkRow
        {
            RowIndex = item.RowIndex, OriginalPath = item.OriginalPath, TypeHint = item.TypeHint,
            SelectedCandidate = item.CandidatePath, Status = RelinkStatus.Updated
        }).ToList();
        if (Hash(original) != intent.SourceSha256)
            throw new InvalidDataException("The source project or relink recovery evidence changed.");
        var expectedOutput = RelinkJsonUpdate.Apply(original, rows);
        if (Hash(expectedOutput) != intent.OutputSha256)
            throw new InvalidDataException("The source project or relink recovery evidence changed.");
        var (openedOutput, needsPublication) = await OpenOutputAsync(intent, evidencePath, resumePreparedOutput, token).ConfigureAwait(false);
        await using var outputLease = openedOutput;
        if (requireStrictPrepared && needsPublication && intent.StrictAssets is null)
            throw new InvalidDataException("Prepared output predates strict name and SHA-256 evidence. Re-run relink while retaining the old record.");
        var output = outputLease.Stream;
        var outputIdentity = GetPhysicalId(output);
        if (outputIdentity == sourceIdentity || output.Length != expectedOutput.Length ||
            await HashAsync(output, token).ConfigureAwait(false) != intent.OutputSha256)
            throw new InvalidDataException("The relink output differs from its saved recovery evidence.");

        var leases = new List<VerifiedFileLease>();
        var strictLeases = new List<AssetPoolVerification>();
        try
        {
            var directory = Path.GetDirectoryName(intent.Output)!;
            var strict = intent.StrictAssets is null ? null
                : new StrictRelinkAssets(intent.Source, Encoding.UTF8.GetString(original), history.Selected.Manifest);
            if (intent.StrictAssets is { } proofs)
            {
                foreach (var proof in proofs)
                {
                    var comparison = intent.Approved.Single(item => item.RowIndex == proof.RowIndex);
                    var match = new AssetPoolMatch(comparison.CandidatePath, proof.ManifestPath, proof.ManifestSha256);
                    strictLeases.Add(await AssetPool.OpenVerifiedAsync(match, proof.Name, comparison.ExpectedSha256!, token).ConfigureAwait(false));
                }
            }
            // Keep exactly the bytes used to derive the revision leased until its publication completes.
            var (review, candidates) = await InspectRowsAsync(directory, history.Selected.Manifest, rows, token, leases, strict).ConfigureAwait(false);
            var derived = AssetManifestStore.Create(intent.Output, candidates, intent.Next.VerifiedAtUtc,
                history.Selected.Manifest, confirmedProjectAssociation: true, confirmedHashChanges: true, projectId: associatedRoot.ProjectId)
                with { RevisionId = intent.OperationId };
            if (!review.SequenceEqual(intent.Approved) ||
                !JsonSerializer.SerializeToUtf8Bytes(derived).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(intent.Next)))
                throw new InvalidDataException("The pending metadata does not match the relink evidence.");
            token.ThrowIfCancellationRequested();
            Action? publishPreparedOutput = needsPublication
                ? () => OwnedFilePublication.RenameOpenedPath(output.SafeFileHandle, intent.Output)
                : null;
            var revision = intent.ObservedTips is { } tips
                ? await AssetRevisionSeries.AppendToBranchOrVerifyAsync(seriesDirectory, associatedRoot, tips, intent.Parent,
                    intent.Next, token, publishPreparedOutput).ConfigureAwait(false)
                : await AssetRevisionSeries.AppendOrVerifyAsync(seriesDirectory, associatedRoot, intent.Parent, intent.Next, token).ConfigureAwait(false);
            return new OutputReceipt(revision, intent.OperationId, intent.Source, intent.SourceSha256,
                intent.Output, intent.OutputSha256, sourceIdentity, outputIdentity);
        }
        finally
        {
            foreach (var lease in leases) await lease.DisposeAsync().ConfigureAwait(false);
            foreach (var lease in strictLeases) await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<Intent> ReadIntentAsync(FileStream stream, string evidencePath, string? expectedSource,
        CancellationToken token)
    {
        if (stream.Length > MaxIntentBytes) throw new InvalidDataException("The relink recovery record is too large.");
        var intent = await JsonSerializer.DeserializeAsync<Intent>(stream, JsonOptions, token).ConfigureAwait(false);
        if (intent is null || intent.Version is not (1 or 2 or 3 or 4 or 5) || intent.Root is null || intent.Next is null ||
            intent.OperationId == Guid.Empty || intent.OperationId != intent.Next.RevisionId || intent.Parent?.ProjectId != intent.Root.ProjectId ||
            intent.Next.ProjectId != intent.Root.ProjectId || intent.Next.ParentRevisionId != intent.Parent.RevisionId ||
            intent.Approved is not { Length: > 0 and <= 10 } ||
            intent.Approved.Any(item => item is null || string.IsNullOrWhiteSpace(item.OriginalPath) || !Path.IsPathFullyQualified(item.CandidatePath) ||
                item.TypeHint is null || !IsHash(item.CandidateSha256)) || !IsHash(intent.SourceSha256) || !IsHash(intent.OutputSha256))
            throw new InvalidDataException("The relink recovery record is invalid.");
        if (intent.Version == 1 ? intent.ObservedTips is not null : !ValidTipEvidence(intent.ObservedTips, intent.Parent))
            throw new InvalidDataException("The relink recovery record has invalid branch evidence.");
        if (intent.Version >= 3 ? intent.SourceIdentity is null || intent.SourceIdentity.FileId == Guid.Empty : intent.SourceIdentity is not null)
            throw new InvalidDataException("The relink recovery record has invalid source identity evidence.");
        ValidatePaths(intent.Source, intent.Output);
        if (intent.Version == 5) ValidateStrictProofs(intent);
        else if (intent.StrictAssets is not null) throw new InvalidDataException("Unexpected strict asset evidence.");
        if (expectedSource is not null && !string.Equals(Path.GetFullPath(expectedSource), intent.Source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This recovery record belongs to a different source project file.");
        AssetManifestStore.Parse(JsonSerializer.SerializeToUtf8Bytes(intent.Next));
        if (!string.Equals(evidencePath, EvidencePath(intent.Source, intent.OperationId), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The relink recovery record was moved or renamed.");
        if (intent.Next.ProjectFileName != Path.GetFileName(intent.Output))
            throw new InvalidDataException("The metadata revision targets a different output.");
        return intent;
    }

    private static void ValidateStrictProofs(Intent intent)
    {
        var directory = Path.GetDirectoryName(intent.Source)!;
        if (intent.StrictAssets is not { } proofs || proofs.Length != intent.Approved.Length ||
            proofs.Any(proof => proof is null) || proofs.Select(proof => proof.RowIndex).Distinct().Count() != proofs.Length ||
            intent.Approved.Select(item => item.RowIndex).Distinct().Count() != proofs.Length)
            throw new InvalidDataException("Incomplete strict relink evidence.");
        foreach (var proof in proofs)
        {
            var comparison = intent.Approved.SingleOrDefault(item => item.RowIndex == proof.RowIndex);
            if (comparison is null || !IsHash(comparison.ExpectedSha256) || comparison.RequiresConfirmation ||
                !string.Equals(proof.Name, AssetPool.GetOriginalFileName(comparison.OriginalPath), StringComparison.OrdinalIgnoreCase) ||
                !IsExactOrdinaryDescendant(directory, comparison.CandidatePath) ||
                (proof.ManifestPath is null ? proof.ManifestSha256 is not null :
                    !IsHash(proof.ManifestSha256) || !IsExactOrdinaryDescendant(directory, proof.ManifestPath) ||
                    !string.Equals(Path.GetDirectoryName(proof.ManifestPath), Path.GetDirectoryName(comparison.CandidatePath), StringComparison.Ordinal) ||
                    !string.Equals(Path.GetFileName(proof.ManifestPath), AssetPool.ManifestFileName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Invalid or unapproved strict relink asset evidence.");
        }
    }

    private static bool IsExactOrdinaryDescendant(string directory, string path)
    {
        if (!StrictRelinkAssets.IsDescendant(directory, path)) return false;
        var relative = Path.GetRelativePath(directory, path);
        return AssetInventory.ResolveRelativePath(directory, relative) == path;
    }

    private static bool ValidTipEvidence(AssetRevisionSeries.Head[]? tips, AssetRevisionSeries.Head parent)
        => tips is { Length: > 0 and <= 4096 } &&
            tips.All(tip => tip is not null && tip.ProjectId == parent.ProjectId && tip.RevisionId != Guid.Empty && IsHash(tip.Sha256)) &&
            tips.Select(tip => tip.RevisionId).Distinct().Count() == tips.Length && tips.Contains(parent);

    internal static Task VerifyOutputAsync(OutputReceipt receipt, CancellationToken token)
        => WithVerifiedProjectsAsync(receipt, _ => Task.CompletedTask, token);

    /// <summary>Keeps read leases on both verified files during a short association update.</summary>
    internal static async Task WithVerifiedProjectsAsync(OutputReceipt receipt, Func<CancellationToken, Task> persist, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(persist);
        token.ThrowIfCancellationRequested();
        ValidatePaths(receipt.Source, receipt.Output);
        await using var sourceLease = OpenVerifiedPath(receipt.Source);
        await using var outputLease = OpenVerifiedPath(receipt.Output);
        var source = sourceLease.Stream;
        var output = outputLease.Stream;
        if (source.Length > MaxSourceBytes || output.Length > MaxSourceBytes + MaxIntentBytes ||
            GetPhysicalId(source) != receipt.SourceIdentity || GetPhysicalId(output) != receipt.OutputIdentity ||
            await HashAsync(source, token).ConfigureAwait(false) != receipt.SourceSha256 ||
            await HashAsync(output, token).ConfigureAwait(false) != receipt.OutputSha256)
            throw new InvalidDataException("The source project or relink output changed before association transfer.");
        token.ThrowIfCancellationRequested();
        await persist(token).ConfigureAwait(false);
    }

    private static void ValidatePaths(string source, string output)
    {
        if (!Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(output) ||
            Path.GetFullPath(source) != source || Path.GetFullPath(output) != output ||
            !Path.GetExtension(source).Equals(".ymmp", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(output).Equals(".ymmp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source, output, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(output), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Relink recovery requires a distinct output beside the source project.");
    }

    private static PhysicalFileId GetPhysicalId(FileStream file)
    {
        var (volume, fileId) = ProjectFileEvidence.ReadPhysicalIdentity(file);
        return new PhysicalFileId(volume, fileId);
    }

    private static string EvidencePath(string source, Guid operationId)
        => Path.Combine(Path.GetDirectoryName(source)!, $".ymmpm-relink-{operationId:N}.json");

    private sealed class VerifiedFileLease(OwnedFilePublication.DirectoryFence fence, FileStream stream) : IAsyncDisposable
    {
        public FileStream Stream { get; } = stream;

        public async ValueTask DisposeAsync()
        {
            try { await Stream.DisposeAsync().ConfigureAwait(false); }
            finally { fence.Dispose(); }
        }
    }

    private static VerifiedFileLease OpenVerifiedPath(string path, bool allowRename = false)
    {
        var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(path)!, enforceShare: true);
        try
        {
            var handle = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename);
            try { return new VerifiedFileLease(fence, new FileStream(handle, FileAccess.Read)); }
            catch { handle.Dispose(); throw; }
        }
        catch (IOException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 })
        {
            fence.Dispose();
            throw new FileNotFoundException("The relink file was not found.", path, ex);
        }
        catch
        {
            fence.Dispose();
            throw;
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static async Task<string> HashAsync(Stream stream, CancellationToken token)
        => Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static async Task WriteEvidenceAsync<T>(string path, T record, int maxBytes, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        if (bytes.Length > maxBytes) throw new InvalidDataException("The relink recovery record is too large.");
        using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(path)!);
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        await using var stream = OwnedFilePublication.CreateOwnedOutputFile(temporary);
        var published = false;
        try
        {
            await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            token.ThrowIfCancellationRequested();
            OwnedFilePublication.RenameOpenedPath(stream.SafeFileHandle, path);
            published = true;
        }
        finally
        {
            if (!published) OwnedFilePublication.TryDeleteOpenedPath(stream.SafeFileHandle);
        }
    }
}
