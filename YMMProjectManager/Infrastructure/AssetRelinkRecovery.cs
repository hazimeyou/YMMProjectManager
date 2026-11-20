using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using YMMProjectManager.Infrastructure.Relink;

namespace YMMProjectManager.Infrastructure;

/// <summary>Durable intent for an output that is published separately from its metadata revision.</summary>
internal static class AssetRelinkRecovery
{
    private const int MaxIntentBytes = 8 * 1024 * 1024;
    private const int MaxSourceBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal sealed record Comparison(int RowIndex, string OriginalPath, string CandidatePath, string? ExpectedSha256, string CandidateSha256,
        string TypeHint)
    {
        public bool RequiresConfirmation => !string.Equals(ExpectedSha256, CandidateSha256, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record Intent(int Version, Guid OperationId, string Source, string SourceSha256, string Output, string OutputSha256,
        AssetRevisionSeries.Head Root, AssetRevisionSeries.Head Parent, AssetManifest Next, Comparison[] Approved);

    internal sealed record OutputReceipt(AssetRevisionSeries.Head Revision, Guid OperationId, string Source, string SourceSha256,
        string Output, string OutputSha256);

    internal static async Task<IReadOnlyList<Comparison>> ReviewAsync(RelinkDocumentContext context, string seriesDirectory,
        AssetRevisionSeries.Head root, AssetRevisionSeries.Head parent, CancellationToken token)
    {
        var history = await AssetRevisionSeries.ResolveAsync(seriesDirectory, root.ProjectId, root.RevisionId,
            root.Sha256, parent.RevisionId, parent.Sha256, token).ConfigureAwait(false);
        if (history.Head != parent) throw new InvalidDataException("The metadata head has changed. Reload before creating a revision.");
        var rows = RelinkCandidateVerifier.SnapshotRows(context.Rows.Where(row => row.Status == RelinkStatus.Updated));
        var (review, _) = await InspectRowsAsync(Path.GetDirectoryName(Path.GetFullPath(context.YmmpPath))!, history.Selected.Manifest, rows, token).ConfigureAwait(false);
        return review;
    }

    internal static async Task<string> PrepareAsync(RelinkDocumentContext context, string destination, string seriesDirectory,
        AssetRevisionSeries.Head root, AssetRevisionSeries.Head parent, IReadOnlyList<Comparison> reviewed, bool confirmedMismatches, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(context.YmmpPath);
        var output = Path.GetFullPath(destination);
        ValidatePaths(source, output);
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("The relink output already exists.");
        var original = context.OriginalBytes?.ToArray() ?? throw new InvalidDataException("The source snapshot is missing.");
        if (original.Length > MaxSourceBytes) throw new InvalidDataException("The project is too large for relink recovery.");
        var rows = RelinkCandidateVerifier.SnapshotRows(context.Rows.Where(row => row.Status == RelinkStatus.Updated));
        var history = await AssetRevisionSeries.ResolveAsync(seriesDirectory, root.ProjectId, root.RevisionId,
            root.Sha256, parent.RevisionId, parent.Sha256, token).ConfigureAwait(false);
        if (history.Head != parent) throw new InvalidDataException("The metadata head has changed. Reload before creating a revision.");
        var sourceDirectory = Path.GetDirectoryName(source)!;
        var (review, candidates) = await InspectRowsAsync(sourceDirectory, history.Selected.Manifest, rows, token).ConfigureAwait(false);
        if (!review.SequenceEqual(reviewed)) throw new InvalidDataException("A relink candidate changed after review.");
        if (review.Any(item => item.RequiresConfirmation) && !confirmedMismatches)
            throw new InvalidDataException("Missing or different asset hashes require confirmation.");

        var next = AssetManifestStore.Create(output, candidates, DateTimeOffset.UtcNow,
            history.Selected.Manifest, confirmedProjectAssociation: true, confirmedHashChanges: confirmedMismatches, projectId: root.ProjectId);
        var intent = new Intent(1, next.RevisionId, source, Hash(original), output, Hash(RelinkJsonUpdate.Apply(original, rows)),
            root, parent, next, review);
        await using var sourceLease = OpenVerifiedPath(source);
        if (await HashAsync(sourceLease, token).ConfigureAwait(false) != intent.SourceSha256)
            throw new InvalidDataException("The source project changed before relink preparation.");
        var evidence = EvidencePath(source, intent.OperationId);
        await WriteNewAsync(evidence, intent, token).ConfigureAwait(false);
        return evidence;
    }

    private static async Task<(Comparison[], AssetMetadata[])> InspectRowsAsync(string sourceDirectory, AssetManifest manifest,
        List<RelinkRow> rows, CancellationToken token)
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
                actual = await AssetInventory.InspectAsync(sourceDirectory, relative, prior?.DownloadUrl, token).ConfigureAwait(false);
                candidates.Add(candidate, actual);
            }
            expectedPaths.TryGetValue(Path.GetFullPath(row.OriginalPath, sourceDirectory), out var expected);
            review.Add(new Comparison(row.RowIndex, row.OriginalPath, row.SelectedCandidate, expected?.Sha256, actual.Sha256, row.TypeHint));
        }
        return (review.ToArray(), candidates.Values.ToArray());
    }

    internal static async Task<AssetRevisionSeries.Head> CompleteAsync(string evidence, string seriesDirectory, AssetRevisionSeries.Head associatedRoot,
        CancellationToken token, string? expectedSource = null)
        => (await CompleteForTransferAsync(evidence, seriesDirectory, associatedRoot, token, expectedSource).ConfigureAwait(false)).Revision;

    internal static async Task<OutputReceipt> CompleteForTransferAsync(string evidence, string seriesDirectory, AssetRevisionSeries.Head associatedRoot,
        CancellationToken token, string? expectedSource = null)
    {
        var evidencePath = Path.GetFullPath(evidence);
        await using var intentStream = OpenVerifiedPath(evidencePath);
        if (intentStream.Length > MaxIntentBytes) throw new InvalidDataException("The relink recovery record is too large.");
        var intent = await JsonSerializer.DeserializeAsync<Intent>(intentStream, JsonOptions, token).ConfigureAwait(false);
        if (intent is null || intent.Version != 1 || intent.Root != associatedRoot || intent.Next is null ||
            intent.OperationId == Guid.Empty || intent.OperationId != intent.Next.RevisionId || intent.Parent?.ProjectId != associatedRoot.ProjectId ||
            intent.Next.ProjectId != associatedRoot.ProjectId || intent.Next.ParentRevisionId != intent.Parent.RevisionId ||
            intent.Approved is not { Length: > 0 and <= 10 } ||
            intent.Approved.Any(item => item is null || string.IsNullOrWhiteSpace(item.OriginalPath) || !Path.IsPathFullyQualified(item.CandidatePath) ||
                item.TypeHint is null || !IsHash(item.CandidateSha256)) || !IsHash(intent.SourceSha256) || !IsHash(intent.OutputSha256))
            throw new InvalidDataException("The relink recovery record is invalid or belongs to a different project.");
        ValidatePaths(intent.Source, intent.Output);
        if (expectedSource is not null && !string.Equals(Path.GetFullPath(expectedSource), intent.Source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This recovery record belongs to a different source project file.");
        AssetManifestStore.Parse(JsonSerializer.SerializeToUtf8Bytes(intent.Next));
        if (!string.Equals(evidencePath, EvidencePath(intent.Source, intent.OperationId), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The relink recovery record was moved or renamed.");
        if (intent.Next.ProjectFileName != Path.GetFileName(intent.Output))
            throw new InvalidDataException("The metadata revision targets a different output.");

        var history = await AssetRevisionSeries.ResolveAsync(seriesDirectory, associatedRoot.ProjectId, associatedRoot.RevisionId,
            associatedRoot.Sha256, intent.Parent.RevisionId, intent.Parent.Sha256, token).ConfigureAwait(false);
        await using var source = OpenVerifiedPath(intent.Source);
        if (source.Length > MaxSourceBytes) throw new InvalidDataException("The project is too large for relink recovery.");
        var original = new byte[checked((int)source.Length)];
        await source.ReadExactlyAsync(original, token).ConfigureAwait(false);
        var rows = intent.Approved.Select(item => new RelinkRow
        {
            RowIndex = item.RowIndex, OriginalPath = item.OriginalPath, TypeHint = item.TypeHint,
            SelectedCandidate = item.CandidatePath, Status = RelinkStatus.Updated
        }).ToList();
        if (Hash(original) != intent.SourceSha256 || Hash(RelinkJsonUpdate.Apply(original, rows)) != intent.OutputSha256)
            throw new InvalidDataException("The source project or relink recovery evidence changed.");
        await using var output = OpenVerifiedPath(intent.Output);
        if (await HashAsync(output, token).ConfigureAwait(false) != intent.OutputSha256)
            throw new InvalidDataException("The relink output differs from its saved recovery evidence.");

        var leases = new List<FileStream>();
        try
        {
            var directory = Path.GetDirectoryName(intent.Output)!;
            foreach (var candidate in intent.Approved.DistinctBy(item => Path.GetFullPath(item.CandidatePath), StringComparer.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(directory, candidate.CandidatePath);
                var asset = intent.Next.Assets.SingleOrDefault(item => string.Equals(item.RelativePath, relative, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("The relink record lacks candidate evidence.");
                if (!string.Equals(asset.Sha256, candidate.CandidateSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The relink candidate evidence is inconsistent.");
                var path = AssetInventory.ResolveRelativePath(directory, asset.RelativePath);
                var lease = OpenVerifiedPath(path);
                leases.Add(lease);
                if (lease.Length != asset.Size || !string.Equals(await HashAsync(lease, token).ConfigureAwait(false), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A relink candidate changed after it was selected.");
            }
            var (review, candidates) = await InspectRowsAsync(directory, history.Selected.Manifest, rows, token).ConfigureAwait(false);
            var derived = AssetManifestStore.Create(intent.Output, candidates, intent.Next.VerifiedAtUtc,
                history.Selected.Manifest, confirmedProjectAssociation: true, confirmedHashChanges: true, projectId: associatedRoot.ProjectId)
                with { RevisionId = intent.OperationId };
            if (!review.SequenceEqual(intent.Approved) ||
                !JsonSerializer.SerializeToUtf8Bytes(derived).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(intent.Next)))
                throw new InvalidDataException("The pending metadata does not match the relink evidence.");
            var revision = await AssetRevisionSeries.AppendOrVerifyAsync(seriesDirectory, associatedRoot, intent.Parent, intent.Next, token).ConfigureAwait(false);
            return new OutputReceipt(revision, intent.OperationId, intent.Source, intent.SourceSha256, intent.Output, intent.OutputSha256);
        }
        finally
        {
            foreach (var lease in leases) await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static Task VerifyOutputAsync(OutputReceipt receipt, CancellationToken token)
        => WithVerifiedProjectsAsync(receipt, _ => Task.CompletedTask, token);

    /// <summary>Keeps read leases on both verified files during a short association update.</summary>
    internal static async Task WithVerifiedProjectsAsync(OutputReceipt receipt, Func<CancellationToken, Task> persist, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(persist);
        token.ThrowIfCancellationRequested();
        ValidatePaths(receipt.Source, receipt.Output);
        await using var source = OpenVerifiedPath(receipt.Source);
        await using var output = OpenVerifiedPath(receipt.Output);
        if (source.Length > MaxSourceBytes || output.Length > MaxSourceBytes + MaxIntentBytes ||
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

    private static string EvidencePath(string source, Guid operationId)
        => Path.Combine(Path.GetDirectoryName(source)!, $".ymmpm-relink-{operationId:N}.json");

    private static FileStream OpenVerifiedPath(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("A relink path traverses a reparse point.");
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("A relink path is not a regular file.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static async Task<string> HashAsync(Stream stream, CancellationToken token)
        => Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static async Task WriteNewAsync(string path, Intent intent, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(intent, JsonOptions);
        if (bytes.Length > MaxIntentBytes) throw new InvalidDataException("The relink recovery record is too large.");
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        var owned = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                owned = true;
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path);
            owned = false;
        }
        finally
        {
            if (owned)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
