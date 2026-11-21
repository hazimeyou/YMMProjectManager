using System.IO;
using System.Text;
using System.Text.Json;
using YMMProjectManager.Infrastructure.Relink;

namespace YMMProjectManager.Infrastructure;

internal static partial class AssetRelinkRecovery
{
    private const string ExternalEvidencePrefix = ".ymmpm-relink-external-";
    private const string DerivedDirectoryPrefix = ".ymmpm-derived-";

    internal enum ExternalRecoveryState { ReadyToVerify, NotStaged, Unknown }
    internal sealed record ExternalRecoveryReview(string Source, string Output, IReadOnlyList<string> ApprovedRoots,
        Guid DerivedProjectId, ExternalRecoveryState State);

    internal sealed record DerivedOutputReceipt(OutputReceipt Output, string SeriesDirectory,
        AssetRevisionSeries.Head SourceRoot, AssetRevisionSeries.Head SourceParent);

    private sealed record ExternalIntent(int Version, Guid OperationId, Guid DerivedProjectId, DateTimeOffset CreatedAt,
        string Source, string SourceSha256, PhysicalFileId SourceIdentity, string Output, string OutputSha256,
        AssetRevisionSeries.Head Root, AssetRevisionSeries.Head Parent, AssetRevisionSeries.Head[] ObservedTips,
        string[] ApprovedRoots, Comparison[] Approved, StrictAssetProof[] StrictAssets)
    {
        public string? DerivedRootSha256 { get; init; }
    }

    internal static async Task<string> PrepareExternalOutputAsync(RelinkDocumentContext context, string destination,
        string seriesDirectory, AssetRevisionSeries.Head root, AssetRevisionSeries.Head parent,
        IReadOnlyList<AssetRevisionSeries.Head> observedTips, StrictAssetProof[] proofs,
        IReadOnlyList<string> approvedRoots, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(context.YmmpPath);
        var output = Path.GetFullPath(destination);
        ValidateExternalOutputPaths(source, output);
        if (Path.Exists(output)) throw new IOException("The relink output already exists.");
        var roots = CanonicalizeApprovedRoots(approvedRoots);
        var original = context.OriginalBytes?.ToArray() ?? throw new InvalidDataException("The source snapshot is missing.");
        if (original.Length > MaxSourceBytes) throw new InvalidDataException("The project is too large for relink recovery.");
        var rows = RelinkCandidateVerifier.SnapshotRows(context.Rows.Where(row => row.Status == RelinkStatus.Updated));
        var observed = observedTips.ToArray();
        var strictProofs = proofs.ToArray();
        await using var media = new ExternalAssetPins();
        await using var sourceLease = OpenVerifiedPath(source);
        var sourceHash = Hash(original);
        if (await AssetContentHash.ReadSha256Async(sourceLease.Stream, token).ConfigureAwait(false) != sourceHash)
            throw new InvalidDataException("The source project changed before relink preparation.");

        var evidence = await AssetRevisionSeries.WithVerifiedSourceTipAsync(seriesDirectory, root, parent, observed,
            async (manifest, heldToken) =>
            {
                var expected = new StrictRelinkAssets(source, Encoding.UTF8.GetString(original), manifest);
                var comparisons = rows.Select(row =>
                {
                    var hash = expected.FindExpected(row.OriginalPath)?.Sha256 ??
                        throw new InvalidDataException("The source metadata does not establish the required full asset SHA-256.");
                    return new Comparison(row.RowIndex, row.OriginalPath,
                        row.SelectedCandidate ?? throw new InvalidDataException("A selected relink file is missing."), hash, hash, row.TypeHint);
                }).ToArray();
                var outputBytes = RelinkJsonUpdate.Apply(original, rows);
                var intent = new ExternalIntent(1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
                    source, sourceHash, GetPhysicalId(sourceLease.Stream), output, Hash(outputBytes),
                    root, parent, observed, roots, comparisons, strictProofs);
                ValidateExternalIntent(intent, requireDerivedRootHash: false);
                await VerifyExternalAssetsAsync(intent, roots, media, heldToken).ConfigureAwait(false);
                var derived = BuildDerivedManifest(intent, original, outputBytes, manifest, media);
                intent = intent with { DerivedRootSha256 = AssetRevisionSeries.DescribeRevision(derived).Sha256 };
                var path = ExternalEvidencePath(source, intent.OperationId);
                await WriteEvidenceAsync(path, intent, MaxIntentBytes, heldToken).ConfigureAwait(false);
                return path;
            }, token).ConfigureAwait(false);
        return evidence;
    }

    internal static async Task<ExternalRecoveryReview?> InspectExternalRecoveryAsync(string evidence, string expectedSource,
        CancellationToken token)
    {
        if (!Path.GetFileName(evidence).StartsWith(ExternalEvidencePrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var path = Path.GetFullPath(evidence);
        VerifyExternalEvidenceLocation(path, expectedSource);
        await using var lease = OpenVerifiedPath(path);
        var intent = await ReadExternalIntentAsync(lease.Stream, path, expectedSource, token).ConfigureAwait(false);
        return new ExternalRecoveryReview(intent.Source, intent.Output, Array.AsReadOnly(intent.ApprovedRoots),
            intent.DerivedProjectId, ObserveExternalRecoveryState(path, intent));
    }

    internal static async Task<(bool Success, string? ErrorMessage)> StageExternalOutputAsync(string evidence,
        RelinkDocumentContext context, RelinkSaveService saver, IReadOnlyList<string> approvedRoots, CancellationToken token)
    {
        var path = Path.GetFullPath(evidence);
        VerifyExternalEvidenceLocation(path, context.YmmpPath);
        await using var intentLease = OpenVerifiedPath(path);
        var intent = await ReadExternalIntentAsync(intentLease.Stream, path, context.YmmpPath, token).ConfigureAwait(false);
        if (ObserveExternalRecoveryState(path, intent) != ExternalRecoveryState.NotStaged)
            throw new InvalidDataException("An external relink publication attempt already exists. Preserve its evidence and use recovery.");
        await using var media = new ExternalAssetPins();
        await VerifyExternalAssetsAsync(intent, approvedRoots, media, token).ConfigureAwait(false);
        await using var source = OpenVerifiedPath(intent.Source);
        var original = await ReadVerifiedSourceAsync(source.Stream, intent, token).ConfigureAwait(false);
        if (context.OriginalBytes is not { } observed || Hash(observed) != intent.SourceSha256)
            throw new InvalidDataException("The supplied project snapshot does not match the prepared external relink.");
        var rows = ApprovedRows(intent);
        if (Hash(RelinkJsonUpdate.Apply(original, rows)) != intent.OutputSha256)
            throw new InvalidDataException("The prepared output no longer matches its original project and approved rows.");
        var frozen = new RelinkDocumentContext { YmmpPath = intent.Source, OriginalBytes = original, Rows = rows };
        return await StageOwnedOutputAsync(path, frozen, intent.Output, intent.OperationId, intent.OutputSha256,
            saver, token).ConfigureAwait(false);
    }

    internal static async Task<DerivedOutputReceipt> CompleteExternalOutputAsync(string evidence, string seriesDirectory,
        AssetRevisionSeries.Head associatedRoot, string expectedSource, IReadOnlyList<string> approvedRoots, CancellationToken token,
        Func<DerivedOutputReceipt, CancellationToken, Task>? register = null)
    {
        var path = Path.GetFullPath(evidence);
        VerifyExternalEvidenceLocation(path, expectedSource);
        await using var intentLease = OpenVerifiedPath(path);
        var intent = await ReadExternalIntentAsync(intentLease.Stream, path, expectedSource, token).ConfigureAwait(false);
        if (intent.Root != associatedRoot)
            throw new InvalidDataException("The external relink belongs to a different source project.");
        var state = ObserveExternalRecoveryState(path, intent);
        if (state != ExternalRecoveryState.ReadyToVerify)
            throw new InvalidDataException(state == ExternalRecoveryState.NotStaged
                ? "No output was staged. Keep this record and start a fresh explicitly approved operation."
                : "The publication cannot be determined. Keep the original, output and all recovery records.");
        var derivedDirectory = DerivedSeriesPath(intent);
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(seriesDirectory)), derivedDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The derived metadata destination conflicts with the source series.");

        await using var media = new ExternalAssetPins();
        await using var sourceLease = OpenVerifiedPath(intent.Source);
        var original = await ReadVerifiedSourceAsync(sourceLease.Stream, intent, token).ConfigureAwait(false);
        var expectedOutput = RelinkJsonUpdate.Apply(original, ApprovedRows(intent));
        if (Hash(expectedOutput) != intent.OutputSha256)
            throw new InvalidDataException("The output no longer matches its original project and approved rows.");
        await VerifyExternalAssetsAsync(intent, approvedRoots, media, token).ConfigureAwait(false);

        return await AssetRevisionSeries.WithVerifiedSourceTipAsync(seriesDirectory, associatedRoot,
            intent.Parent, intent.ObservedTips, async (manifest, heldToken) =>
            {
                var derived = BuildDerivedManifest(intent, original, expectedOutput, manifest, media);
                if (AssetRevisionSeries.DescribeRevision(derived).Sha256 != intent.DerivedRootSha256)
                    throw new InvalidDataException("The rederived manifest no longer matches the prepared root SHA-256.");
                var (lease, publish) = await OpenProvenOutputAsync(path, intent.Output, intent.OperationId, intent.OutputSha256,
                    resume: true, heldToken).ConfigureAwait(false);
                await using var outputLease = lease;
                var output = lease.Stream;
                var id = GetPhysicalId(output);
                if (id == intent.SourceIdentity || output.Length != expectedOutput.Length ||
                    await AssetContentHash.ReadSha256Async(output, heldToken).ConfigureAwait(false) != intent.OutputSha256)
                    throw new InvalidDataException("Unknown external relink output. The prepared physical file and bytes are required.");
                var child = await AssetRevisionSeries.PublishOrVerifyDerivedRootAsync(derivedDirectory, derived, heldToken).ConfigureAwait(false);
                return await AssetRevisionSeries.WithVerifiedSourceTipAsync(derivedDirectory, child, child, [child], async (_, childToken) =>
                {
                    childToken.ThrowIfCancellationRequested();
                    if (publish) OwnedFilePublication.RenameOpenedPath(output.SafeFileHandle, intent.Output);
                    // The exclusive rename handle cannot coexist with the regular
                    // read lease. Reopen the final path and prove the same physical file.
                    await outputLease.DisposeAsync().ConfigureAwait(false);
                    var proof = new OutputReceipt(child, intent.OperationId, intent.Source, intent.SourceSha256, intent.Output,
                        intent.OutputSha256, intent.SourceIdentity, id);
                    var receipt = new DerivedOutputReceipt(proof, derivedDirectory, intent.Root, intent.Parent);
                    await WithVerifiedProjectsAsync(proof, verifiedToken =>
                        register is null ? Task.CompletedTask : register(receipt, verifiedToken), childToken).ConfigureAwait(false);
                    return receipt;
                }, heldToken).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
    }

    private static AssetManifest BuildDerivedManifest(ExternalIntent intent, byte[] original, byte[] output, AssetManifest sourceManifest,
        ExternalAssetPins media)
    {
        var expected = new StrictRelinkAssets(intent.Source, Encoding.UTF8.GetString(original), sourceManifest);
        var changes = new Dictionary<string, AssetMetadata>(StringComparer.OrdinalIgnoreCase);
        var expectedDestinations = new List<(string Path, AssetMetadata Asset)>();
        foreach (var row in intent.Approved)
        {
            var proof = intent.StrictAssets.Single(item => item.RowIndex == row.RowIndex);
            var asset = expected.FindExpected(row.OriginalPath);
            if (asset is null || !string.Equals(asset.Sha256, row.ExpectedSha256, StringComparison.OrdinalIgnoreCase) ||
                asset.Size != media.VerifiedSizes[row.CandidatePath] ||
                !string.Equals(asset.ExternalRelink?.OriginalFileName ?? AssetPool.GetOriginalFileName(row.OriginalPath),
                    proof.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The original asset name or full SHA-256 no longer agrees with the selected revision.");
            var rootIndex = Array.FindIndex(intent.ApprovedRoots, root => StrictRelinkAssets.IsDescendant(root, row.CandidatePath));
            var location = new ExternalAssetLocation($"relink-{intent.DerivedProjectId:N}-{rootIndex + 1}",
                Path.GetRelativePath(intent.ApprovedRoots[rootIndex], row.CandidatePath));
            var replacement = asset with
            {
                ExternalLocation = location,
                ExternalRelink = new ExternalRelinkBinding(proof.Name, StrictRelinkAssets.ReferenceSha256(row.CandidatePath), proof.ManifestSha256),
            };
            if (changes.TryGetValue(asset.RelativePath, out var previous) && previous != replacement)
                throw new InvalidDataException("Select one consistent destination for every occurrence of the same asset.");
            changes[asset.RelativePath] = replacement;
            expectedDestinations.Add((row.CandidatePath, replacement));
        }
        var assets = sourceManifest.Assets.Select(asset => changes.GetValueOrDefault(asset.RelativePath) ?? asset).ToArray();
        var derived = AssetManifestStore.Create(intent.Output, assets, intent.CreatedAt, projectId: intent.DerivedProjectId) with
        {
            RevisionId = intent.OperationId,
            DerivedFrom = new AssetDerivation(intent.Root.ProjectId, intent.Root.RevisionId, intent.Root.Sha256,
                intent.Parent.RevisionId, intent.Parent.Sha256, intent.SourceSha256),
        };
        var resolved = new StrictRelinkAssets(intent.Output, Encoding.UTF8.GetString(output), derived);
        if (expectedDestinations.Any(item => resolved.FindExpected(item.Path) != item.Asset))
            throw new InvalidDataException("The derived project would resolve an asset to an incompatible metadata entry.");
        return derived;
    }

    private static async Task VerifyExternalAssetsAsync(ExternalIntent intent, IReadOnlyList<string> approvedRoots, ExternalAssetPins media,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var roots = CanonicalizeApprovedRoots(approvedRoots);
        if (!roots.SequenceEqual(intent.ApprovedRoots, StringComparer.Ordinal))
            throw new InvalidDataException("Explicitly approve the original search locations again before using this recovery record.");
        foreach (var root in roots)
            media.Roots.Add(OwnedFilePublication.DirectoryFence.Open(root, enforceShare: true));
        foreach (var proof in intent.StrictAssets)
        {
            var row = intent.Approved.Single(item => item.RowIndex == proof.RowIndex);
            var candidate = new AssetPoolMatch(row.CandidatePath, proof.ManifestPath, proof.ManifestSha256);
            var asset = await AssetPool.OpenVerifiedAsync(candidate, proof.Name, row.ExpectedSha256!, token).ConfigureAwait(false);
            media.Assets.Add(asset);
            media.VerifiedSizes[row.CandidatePath] = new FileInfo(asset.CandidatePath).Length;
        }
    }

    private static List<RelinkRow> ApprovedRows(ExternalIntent intent) => intent.Approved.Select(row => new RelinkRow
    {
        RowIndex = row.RowIndex, OriginalPath = row.OriginalPath, SelectedCandidate = row.CandidatePath, TypeHint = row.TypeHint,
        Status = RelinkStatus.Updated,
    }).ToList();

    private static async Task<byte[]> ReadVerifiedSourceAsync(FileStream source, ExternalIntent intent, CancellationToken token)
    {
        if (source.Length is <= 0 or > MaxSourceBytes || GetPhysicalId(source) != intent.SourceIdentity)
            throw new InvalidDataException("The original project was replaced or cannot be read.");
        var original = new byte[(int)source.Length];
        await source.ReadExactlyAsync(original, token).ConfigureAwait(false);
        if (Hash(original) != intent.SourceSha256)
            throw new InvalidDataException("The original project changed after external relink preparation.");
        return original;
    }

    private static async Task<ExternalIntent> ReadExternalIntentAsync(FileStream stream, string evidence, string expectedSource,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (stream.Length is <= 0 or > MaxIntentBytes)
            throw new InvalidDataException("The external relink recovery record has an invalid size.");
        var intent = await JsonSerializer.DeserializeAsync<ExternalIntent>(stream, JsonOptions, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The external relink recovery record is missing.");
        if (!string.Equals(intent.Source, Path.GetFullPath(expectedSource), StringComparison.Ordinal) ||
            !string.Equals(evidence, ExternalEvidencePath(intent.Source, intent.OperationId), StringComparison.Ordinal))
            throw new InvalidDataException("The external relink recovery record was moved or belongs to another project.");
        ValidateExternalIntent(intent);
        return intent;
    }

    private static void ValidateExternalIntent(ExternalIntent intent, bool requireDerivedRootHash = true)
    {
        if (intent.Version != 1 || intent.OperationId == Guid.Empty || intent.DerivedProjectId == Guid.Empty ||
            intent.CreatedAt == default || intent.Root is null || intent.Parent is null ||
            intent.Root.ProjectId == Guid.Empty || intent.Root.ProjectId != intent.Parent.ProjectId ||
            intent.DerivedProjectId == intent.Root.ProjectId || intent.Root.RevisionId == Guid.Empty || intent.Parent.RevisionId == Guid.Empty ||
            !IsHash(intent.Root.Sha256) || !IsHash(intent.Parent.Sha256) || !IsHash(intent.SourceSha256) || !IsHash(intent.OutputSha256) ||
            (requireDerivedRootHash && !IsHash(intent.DerivedRootSha256)) ||
            intent.SourceIdentity is null || intent.SourceIdentity.FileId == Guid.Empty ||
            !ValidTipEvidence(intent.ObservedTips, intent.Parent) || intent.ApprovedRoots is not { Length: > 0 and <= 10 } ||
            !intent.ApprovedRoots.SequenceEqual(CanonicalizeApprovedRoots(intent.ApprovedRoots), StringComparer.Ordinal) ||
            intent.Approved is not { Length: > 0 and <= 10 } || intent.StrictAssets is null ||
            intent.StrictAssets.Length != intent.Approved.Length || intent.StrictAssets.Any(item => item is null) ||
            intent.StrictAssets.Select(item => item.RowIndex).Distinct().Count() != intent.StrictAssets.Length ||
            intent.Approved.Any(item => item is null) || intent.Approved.Select(item => item.RowIndex).Distinct().Count() != intent.Approved.Length)
            throw new InvalidDataException("Invalid external relink recovery data.");
        ValidateExternalOutputPaths(intent.Source, intent.Output);
        if (intent.Approved.Any(row => string.IsNullOrWhiteSpace(row.OriginalPath) || row.TypeHint is null ||
            !IsHash(row.ExpectedSha256) || row.RequiresConfirmation || !IsCanonicalOrdinaryPath(row.CandidatePath) ||
            !intent.ApprovedRoots.Any(root => StrictRelinkAssets.IsDescendant(root, row.CandidatePath))))
            throw new InvalidDataException("A relink candidate is outside the approved locations or has invalid evidence.");
        foreach (var proof in intent.StrictAssets)
        {
            var row = intent.Approved.SingleOrDefault(item => item.RowIndex == proof.RowIndex);
            if (row is null || proof.Name is null || !AssetInventory.IsOrdinaryPathComponent(proof.Name) ||
                (proof.ManifestPath is null
                    ? proof.ManifestSha256 is not null || !string.Equals(proof.Name, Path.GetFileName(row.CandidatePath), StringComparison.OrdinalIgnoreCase)
                    : !IsHash(proof.ManifestSha256) || !IsCanonicalOrdinaryPath(proof.ManifestPath) ||
                    !string.Equals(Path.GetDirectoryName(proof.ManifestPath), Path.GetDirectoryName(row.CandidatePath), StringComparison.Ordinal) ||
                    !string.Equals(Path.GetFileName(proof.ManifestPath), AssetPool.ManifestFileName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Invalid pool manifest or original name evidence.");
        }
    }

    private static string[] CanonicalizeApprovedRoots(IReadOnlyList<string> approvedRoots)
    {
        if (approvedRoots is not { Count: > 0 and <= 10 } ||
            approvedRoots.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
            throw new InvalidDataException("A confirmed search location is required.");
        var roots = approvedRoots.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (roots.Any(root => !IsCanonicalOrdinaryPath(root)) ||
            roots.Any(root => roots.Any(other => other != root && StrictRelinkAssets.IsDescendant(other, root))))
            throw new InvalidDataException("Approved search locations must be ordinary, distinct directories.");
        return roots;
    }

    private static bool IsCanonicalOrdinaryPath(string? path)
    {
        if (path is null) return false;
        try { return AssetPool.FullOrdinaryPath(path) == path; }
        catch (ArgumentException) { return false; }
    }

    private static void ValidateExternalOutputPaths(string source, string output)
    {
        ValidatePaths(source, output);
        if (!IsCanonicalOrdinaryPath(source) || !IsCanonicalOrdinaryPath(output) ||
            !string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(output), StringComparison.Ordinal))
            throw new InvalidDataException("External relink requires an ordinary new output beside the original project.");
    }

    private static void VerifyExternalEvidenceLocation(string evidence, string source)
    {
        if (!IsCanonicalOrdinaryPath(evidence) ||
            !string.Equals(Path.GetDirectoryName(evidence), Path.GetDirectoryName(Path.GetFullPath(source)), StringComparison.Ordinal) ||
            !Path.GetFileName(evidence).StartsWith(ExternalEvidencePrefix, StringComparison.Ordinal) ||
            !Path.GetExtension(evidence).Equals(".json", StringComparison.Ordinal))
            throw new InvalidDataException("Choose an external relink recovery record beside its original project.");
    }

    private static string ExternalEvidencePath(string source, Guid operationId) =>
        Path.Combine(Path.GetDirectoryName(source)!, $"{ExternalEvidencePrefix}{operationId:N}.json");

    private static string DerivedSeriesPath(ExternalIntent intent) =>
        Path.Combine(Path.GetDirectoryName(intent.Source)!, $"{DerivedDirectoryPrefix}{intent.DerivedProjectId:N}");

    private enum ExternalArtifact { Missing, Ordinary, Uncertain }

    private static ExternalRecoveryState ObserveExternalRecoveryState(string evidence, ExternalIntent intent)
    {
        var witness = ObserveArtifact(PublicationPath(evidence));
        var staged = ObserveArtifact(StagedOutputPath(evidence));
        var output = ObserveArtifact(intent.Output);
        var child = ObserveArtifact(DerivedSeriesPath(intent), directory: true);
        if (witness == ExternalArtifact.Ordinary &&
            ((staged == ExternalArtifact.Ordinary && output == ExternalArtifact.Missing) ||
             (staged == ExternalArtifact.Missing && output == ExternalArtifact.Ordinary)))
            return ExternalRecoveryState.ReadyToVerify;
        return witness == ExternalArtifact.Missing && staged == ExternalArtifact.Missing &&
            output == ExternalArtifact.Missing && child == ExternalArtifact.Missing
            ? ExternalRecoveryState.NotStaged : ExternalRecoveryState.Unknown;
    }

    private static ExternalArtifact ObserveArtifact(string path, bool directory = false)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            var expected = directory ? FileAttributes.Directory : 0;
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == expected
                ? ExternalArtifact.Ordinary : ExternalArtifact.Uncertain;
        }
        catch (FileNotFoundException) { return ExternalArtifact.Missing; }
        catch (DirectoryNotFoundException) { return ExternalArtifact.Missing; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ExternalArtifact.Uncertain; }
    }

    private sealed class ExternalAssetPins : IAsyncDisposable
    {
        internal List<AssetPoolVerification> Assets { get; } = [];
        internal List<OwnedFilePublication.DirectoryFence> Roots { get; } = [];
        internal Dictionary<string, long> VerifiedSizes { get; } = new(StringComparer.Ordinal);

        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (var asset in Assets) await asset.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                foreach (var root in Roots) root.Dispose();
            }
        }
    }
}
