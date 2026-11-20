using System.IO;
using System.Text.Json;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Relink;

internal static class AssetManifestTests
{
    public static async Task RunAsync(string workRoot)
    {
        var root = Path.Combine(workRoot, "asset-manifest"); Directory.CreateDirectory(root);
        var project = Path.Combine(root, "project.ymmp");
        await File.WriteAllTextAsync(project, "{}"); await File.WriteAllTextAsync(Path.Combine(root, "asset.txt"), "asset");
        var item = await AssetInventory.InspectAsync(root, "asset.txt");
        await File.WriteAllTextAsync(Path.Combine(root, "second.txt"), "second");
        var second = await AssetInventory.InspectAsync(root, "second.txt");
        try { AssetManifestStore.Create(project, [item, item with { RelativePath = ".\\asset.txt" }], DateTimeOffset.UtcNow); throw new Exception("duplicate paths accepted"); } catch (InvalidDataException) { }
        var initial = AssetManifestStore.Create(project, [item, second], DateTimeOffset.UtcNow);
        var path = Path.Combine(root, "manifest-1.json"); await AssetManifestStore.SaveNewAsync(path, initial);
        var loaded = await AssetManifestStore.LoadAsync(path);
        var snapshot = await File.ReadAllBytesAsync(path);
        var parsedSnapshot = AssetManifestStore.Parse(snapshot);
        if (parsedSnapshot.ProjectId != initial.ProjectId || parsedSnapshot.RevisionId != initial.RevisionId) throw new Exception("metadata snapshot parsed incorrectly");
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(snapshot).ToArray();
        if (AssetManifestStore.Parse(withBom).ProjectId != initial.ProjectId) throw new Exception("UTF-8 BOM metadata rejected");
        var bomPath = Path.Combine(root, "bom.json"); await File.WriteAllBytesAsync(bomPath, withBom);
        if ((await AssetManifestStore.LoadAsync(bomPath)).ProjectId != initial.ProjectId) throw new Exception("UTF-8 BOM metadata load rejected");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(initial with { ProjectId = Guid.NewGuid() }));
        if (AssetManifestStore.Parse(snapshot).ProjectId != initial.ProjectId) throw new Exception("captured metadata changed with file");
        await File.WriteAllBytesAsync(path, snapshot);
        var identity = Guid.NewGuid();
        var associated = AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, projectId: identity);
        if (associated.ProjectId != identity) throw new Exception("project identity not associated");
        var renamed = AssetManifestStore.Create(Path.Combine(root, "renamed.ymmp"), [item], DateTimeOffset.UtcNow, associated, true, projectId: identity);
        if (renamed.ProjectId != identity || renamed.ParentRevisionId != associated.RevisionId) throw new Exception("rename lost identity");
        try { AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, associated, true, projectId: Guid.NewGuid()); throw new Exception("wrong project identity accepted"); } catch (InvalidDataException) { }
        try { AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, projectId: Guid.Empty); throw new Exception("empty identity accepted"); } catch (InvalidDataException) { }
        var candidate = Path.Combine(root, "candidate.txt"); await File.WriteAllTextAsync(candidate, "asset");
        var row = new RelinkRow { OriginalPath = "asset.txt", SelectedCandidate = candidate, Status = RelinkStatus.Updated };
        var matching = await RelinkCandidateVerifier.CompareExpectedAsync([row], project, identity, associated, CancellationToken.None);
        if (!matching.Single().Contains("判定: 一致")) throw new Exception("expected hash match missed");
        await File.WriteAllTextAsync(candidate, "changed");
        var differing = await RelinkCandidateVerifier.CompareExpectedAsync([row], project, identity, associated, CancellationToken.None);
        if (!differing.Single().Contains("判定: 不一致")) throw new Exception("expected hash mismatch missed");
        await File.WriteAllTextAsync(candidate, "asset");
        var reverted = await RelinkCandidateVerifier.CompareExpectedAsync([row], project, identity, associated, CancellationToken.None);
        if (differing.SequenceEqual(reverted)) throw new Exception("candidate changed after displayed comparison without detection");
        var unknownRow = new RelinkRow { OriginalPath = "unrecorded.txt", SelectedCandidate = candidate, Status = RelinkStatus.Updated };
        if (!(await RelinkCandidateVerifier.CompareExpectedAsync([unknownRow], project, identity, associated, CancellationToken.None)).Single().Contains("期待ハッシュなし")) throw new Exception("missing expected hash misreported");
        try { await RelinkCandidateVerifier.CompareExpectedAsync([row], project, Guid.NewGuid(), associated, CancellationToken.None); throw new Exception("wrong identity accepted for comparison"); } catch (InvalidDataException) { }
        if (loaded.ProjectId != initial.ProjectId || loaded.Assets.Count != 2) throw new Exception("metadata read failed");
        try { AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, loaded); throw new Exception("unconfirmed association accepted"); } catch (InvalidDataException) { }
        var next = AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, loaded, confirmedProjectAssociation: true);
        if (next.ProjectId != initial.ProjectId || next.ParentRevisionId != initial.RevisionId || next.Assets.Count != 2 || !next.Assets.Contains(second)) throw new Exception("revision identity or retention failed");
        var changed = item with { Sha256 = new string('A', 64) };
        if (AssetManifestStore.FindChangedPaths(loaded, [changed]).Count != 1) throw new Exception("hash mismatch not detected");
        try { AssetManifestStore.Create(project, [changed], DateTimeOffset.UtcNow, loaded, true); throw new Exception("unconfirmed hash change accepted"); } catch (InvalidDataException) { }
        var confirmed = AssetManifestStore.Create(project, [changed], DateTimeOffset.UtcNow, loaded, true, true);
        if (confirmed.Assets[0].Sha256 != changed.Sha256 || !confirmed.Assets.Contains(second)) throw new Exception("confirmed hash change failed");
        var original = await File.ReadAllBytesAsync(path);
        try { await AssetManifestStore.SaveNewAsync(path, next); throw new Exception("overwrite accepted"); } catch (IOException) { }
        var after = await File.ReadAllBytesAsync(path);
        if (!original.SequenceEqual(after)) throw new Exception("existing metadata changed");
        try { AssetManifestStore.Create(Path.Combine(root, "renamed.ymmp"), [item], DateTimeOffset.UtcNow, loaded, true); throw new Exception("project mismatch accepted"); } catch (InvalidDataException) { }
        var bad = Path.Combine(root, "bad.json");
        await File.WriteAllTextAsync(bad, JsonSerializer.Serialize(initial with { SchemaVersion = 999 }));
        try { await AssetManifestStore.LoadAsync(bad); throw new Exception("unknown schema accepted"); } catch (InvalidDataException) { }
        var extra = Path.Combine(root, "unknown.json");
        await File.WriteAllTextAsync(extra, JsonSerializer.Serialize(initial).Insert(1, "\"UnknownProperty\":true,"));
        try { await AssetManifestStore.LoadAsync(extra); throw new Exception("unknown field accepted"); } catch (JsonException) { }
        var racePath = Path.Combine(root, "race.json");
        async Task<bool> Save(AssetManifest value) { try { await AssetManifestStore.SaveNewAsync(racePath, value); return true; } catch (IOException) { return false; } }
        var winners = await Task.WhenAll(Save(next), Save(AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, loaded, true)));
        if (winners.Count(x => x) != 1 || (await AssetManifestStore.LoadAsync(racePath)).ProjectId != initial.ProjectId) throw new Exception("concurrent revisions overwritten");
        await TestRevisionSeriesAsync(root, project, item);
        await TestRelinkRecoveryAsync(root);
        Console.WriteLine("Project asset immutable revision checks passed.");
    }

    private static async Task TestRelinkRecoveryAsync(string root)
    {
        var folder = Path.Combine(root, "managed-relink");
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "source.ymmp");
        var output = Path.Combine(folder, "output.ymmp");
        var original = Path.Combine(folder, "original.wav");
        var candidate = Path.Combine(folder, "candidate.wav");
        var seriesDirectory = Path.Combine(folder, "series");
        Directory.CreateDirectory(seriesDirectory);
        await File.WriteAllTextAsync(source, """{"Items":[{"FilePath":"original.wav"}]}""");
        await File.WriteAllTextAsync(original, "original asset");
        await File.WriteAllTextAsync(candidate, "replacement asset");
        var bytes = await File.ReadAllBytesAsync(source);
        var originalAsset = await AssetInventory.InspectAsync(folder, "original.wav");
        var initial = AssetManifestStore.Create(source, [originalAsset], DateTimeOffset.UtcNow);
        var head = await AssetRevisionSeries.InitializeAsync(seriesDirectory, initial);
        var context = new RelinkDocumentContext
        {
            YmmpPath = source, OriginalBytes = bytes,
            Rows = [new RelinkRow { OriginalPath = "original.wav", SelectedCandidate = candidate, Status = RelinkStatus.Updated }]
        };
        var review = await AssetRelinkRecovery.ReviewAsync(context, seriesDirectory, head, head, default);
        if (review.Count != 1 || !review[0].RequiresConfirmation) throw new Exception("replacement hash mismatch was not displayed");
        await RejectAsync(() => AssetRelinkRecovery.PrepareAsync(context, output, seriesDirectory, head, head, review, false, default));
        await File.WriteAllTextAsync(candidate, "another asset");
        await RejectAsync(() => AssetRelinkRecovery.PrepareAsync(context, output, seriesDirectory, head, head, review, true, default));
        await File.WriteAllTextAsync(candidate, "replacement asset");
        var evidence = await AssetRelinkRecovery.PrepareAsync(context, output, seriesDirectory, head, head, review, true, default);
        try
        {
            await AssetRelinkRecovery.CompleteAsync(evidence, seriesDirectory, head, default);
            throw new Exception("revision written before output publication");
        }
        catch (FileNotFoundException) { }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try
            {
                await AssetRelinkRecovery.CompleteAsync(evidence, seriesDirectory, head, cancelled.Token);
                throw new Exception("cancelled recovery accepted");
            }
            catch (OperationCanceledException) { }
        }
        var saver = new RelinkSaveService(new FileLogger(Path.Combine(folder, "test.log")));
        if (!(await saver.SaveAsAsync(context, output, default)).Success) throw new Exception("relink SaveAs failed");
        var published = await File.ReadAllBytesAsync(output);
        await File.WriteAllTextAsync(candidate, "another asset");
        await RejectAsync(() => AssetRelinkRecovery.CompleteAsync(evidence, seriesDirectory, head, default));
        await File.WriteAllTextAsync(candidate, "replacement asset");
        await File.WriteAllTextAsync(output, "{}");
        await RejectAsync(() => AssetRelinkRecovery.CompleteAsync(evidence, seriesDirectory, head, default));
        await File.WriteAllBytesAsync(output, published);
        if (await AssetRevisionSeries.ReadHeadAsync(seriesDirectory) != head) throw new Exception("a failed relink changed the metadata head");
        var transfer = await AssetRelinkRecovery.CompleteForTransferAsync(evidence, seriesDirectory, head, default, source);
        var completed = transfer.Revision;
        var repeated = await AssetRelinkRecovery.CompleteAsync(evidence, seriesDirectory, head, default);
        if (transfer.Source != source || transfer.Output != output || transfer.OperationId != completed.RevisionId ||
            transfer.SourceSha256 != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) ||
            transfer.OutputSha256 != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(published)))
            throw new Exception("verified transfer receipt did not bind the original source and output");
        await File.AppendAllTextAsync(output, " ");
        await RejectAsync(() => AssetRelinkRecovery.WithVerifiedProjectsAsync(transfer,
            _ => throw new Exception("association transfer ran for a changed output"), default));
        await File.WriteAllBytesAsync(output, published);
        var persisted = false;
        await AssetRelinkRecovery.WithVerifiedProjectsAsync(transfer, _ =>
        {
            foreach (var path in new[] { source, output })
            {
                try
                {
                    using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
                    throw new Exception("the transfer let another writer open a verified file");
                }
                catch (IOException) { }
            }
            persisted = true;
            return Task.CompletedTask;
        }, default);
        if (!persisted) throw new Exception("association transfer did not run with valid files");
        var rootFile = Path.Combine(seriesDirectory, $"revision-{head.RevisionId:N}.json");
        var rootBytes = await File.ReadAllBytesAsync(rootFile);
        await File.AppendAllTextAsync(rootFile, " ");
        var observed = await AssetRevisionSeries.InspectAsync(seriesDirectory);
        if (observed.Head != completed || observed.Root == head)
            throw new Exception("the fixture must change only the root receipt");
        var uncommitted = AssetManifestStore.Create(output, [], DateTimeOffset.UtcNow,
            observed.Revisions[^1].Manifest, true, projectId: head.ProjectId);
        await RejectAsync(() => AssetRevisionSeries.AppendAsync(seriesDirectory, head, completed, uncommitted));
        await RejectAsync(() => AssetRelinkRecovery.CompleteAsync(evidence, seriesDirectory, head, default));
        await File.WriteAllBytesAsync(rootFile, rootBytes);
        var pendingOutput = Path.Combine(folder, "pending.ymmp");
        var pendingReview = await AssetRelinkRecovery.ReviewAsync(context, seriesDirectory, head, completed, default);
        var pending = await AssetRelinkRecovery.PrepareAsync(context, pendingOutput, seriesDirectory, head, completed, pendingReview, true, default);
        if (!(await saver.SaveAsAsync(context, pendingOutput, default)).Success) throw new Exception("pending relink SaveAs failed");
        var competing = AssetManifestStore.Create(output, [], DateTimeOffset.UtcNow,
            (await AssetRevisionSeries.InspectAsync(seriesDirectory)).Revisions[^1].Manifest, true, projectId: head.ProjectId);
        var competingHead = await AssetRevisionSeries.AppendOrVerifyAsync(seriesDirectory, completed, competing);
        await RejectAsync(() => AssetRelinkRecovery.CompleteAsync(pending, seriesDirectory, head, default));
        var pendingBytes = await File.ReadAllBytesAsync(pendingOutput);
        var history = await AssetRevisionSeries.InspectAsync(seriesDirectory);
        var preservedSource = await File.ReadAllBytesAsync(source);
        var preservedOutput = await File.ReadAllBytesAsync(output);
        if (repeated != completed || history.Head != competingHead || history.Revisions.Count != 3 ||
            history.Revisions[1].Manifest.Assets.Count != 2 || !bytes.SequenceEqual(preservedSource) ||
            !published.SequenceEqual(preservedOutput) || !published.SequenceEqual(pendingBytes) || !File.Exists(pending))
            throw new Exception("relink recovery duplicated a revision or failed to preserve existing or pending data");

        static async Task RejectAsync(Func<Task> run)
        {
            try { await run(); throw new Exception("invalid relink state accepted"); }
            catch (InvalidDataException) { }
        }
    }

    private static async Task TestRevisionSeriesAsync(string root, string project, AssetMetadata item)
    {
        var directory = Path.Combine(root, "managed-series"); Directory.CreateDirectory(directory);
        var initial = AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, projectId: Guid.NewGuid());
        var head = await AssetRevisionSeries.InitializeAsync(directory, initial);
        if (head != await AssetRevisionSeries.ReadHeadAsync(directory)) throw new Exception("series head did not persist");
        using (var held = new FileStream(Path.Combine(directory, ".asset-revisions.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { await AssetRevisionSeries.ReadHeadAsync(directory); throw new Exception("held series lock was ignored"); } catch (IOException) { }
        }
        var first = AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, initial, true, projectId: initial.ProjectId);
        var second = AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, initial, true, projectId: initial.ProjectId);
        var firstPath = Path.Combine(directory, $"revision-{initial.RevisionId:N}.json");
        var oldBytes = await File.ReadAllBytesAsync(firstPath);
        try { await AssetRevisionSeries.InitializeAsync(directory, initial); throw new Exception("series reinitialization accepted"); } catch (InvalidDataException) { }
        var afterReinitialize = await File.ReadAllBytesAsync(firstPath);
        if (!oldBytes.SequenceEqual(afterReinitialize)) throw new Exception("reinitialization altered existing revision");
        async Task<bool> Append(AssetManifest revision)
        {
            try { await AssetRevisionSeries.AppendAsync(directory, head, revision); return true; }
            catch (IOException) { return false; }
            catch (InvalidDataException) { return false; }
        }
        var results = await Task.WhenAll(Append(first), Append(second));
        if (results.Count(x => x) != 1) throw new Exception("concurrent head updates did not produce exactly one winner");
        var firstAfterUpdate = await File.ReadAllBytesAsync(firstPath);
        if (!oldBytes.SequenceEqual(firstAfterUpdate)) throw new Exception("series update changed historical revision");
        var current = await AssetRevisionSeries.ReadHeadAsync(directory);
        if (current.ProjectId != initial.ProjectId || (current.RevisionId != first.RevisionId && current.RevisionId != second.RevisionId)) throw new Exception("incorrect current head");
        var history = await AssetRevisionSeries.InspectAsync(directory);
        if (history.Root != head || history.Head != current ||
            !history.Revisions.Select(r => r.Receipt).SequenceEqual(new[] { head, current }))
            throw new Exception("series inspection lost causal order or receipt identity");
        var selection = await AssetRevisionSeries.ResolveAsync(directory, head.ProjectId, head.RevisionId, head.Sha256, head.RevisionId, head.Sha256);
        if (selection.Selected.Manifest.RevisionId != initial.RevisionId || selection.Head != current)
            throw new Exception("historical selection was lost after a newer revision");
        var importPath = Path.Combine(root, "imported-history");
        var winningPath = Path.Combine(directory, $"revision-{current.RevisionId:N}.json");
        var imported = await AssetRevisionSeries.ImportLegacyAsync(importPath, head.ProjectId, [winningPath, firstPath]);
        if (!imported.Revisions.Select(x => x.Receipt).SequenceEqual(history.Revisions.Select(x => x.Receipt)))
            throw new Exception("legacy history import did not preserve the original revision bytes and order");
        try
        {
            await AssetRevisionSeries.ImportLegacyAsync(importPath, head.ProjectId, [firstPath]);
            throw new Exception("legacy import accepted an existing destination");
        }
        catch (IOException) { }
        if ((await AssetRevisionSeries.ReadHeadAsync(importPath)) != current) throw new Exception("repeated import altered the published history");
        var orphanImport = Path.Combine(root, "incomplete-import");
        try
        {
            await AssetRevisionSeries.ImportLegacyAsync(orphanImport, head.ProjectId, [winningPath]);
            throw new Exception("legacy import accepted an orphan revision");
        }
        catch (InvalidDataException) { }
        if (Directory.Exists(orphanImport)) throw new Exception("invalid history became visible as an imported series");
        using (var cancelledImport = new CancellationTokenSource())
        {
            cancelledImport.Cancel();
            try
            {
                await AssetRevisionSeries.ImportLegacyAsync(orphanImport, head.ProjectId, [firstPath], cancelledImport.Token);
                throw new Exception("legacy import ignored cancellation");
            }
            catch (OperationCanceledException) { }
        }
        if (Directory.Exists(orphanImport)) throw new Exception("cancelled history import created its destination");
        try
        {
            await AssetRevisionSeries.ResolveAsync(directory, Guid.NewGuid(), head.RevisionId, head.Sha256, current.RevisionId, current.Sha256);
            throw new Exception("foreign metadata association accepted");
        }
        catch (InvalidDataException) { }
        try
        {
            await AssetRevisionSeries.ResolveAsync(directory, head.ProjectId, head.RevisionId, head.Sha256, current.RevisionId, new string('0', 64));
            throw new Exception("changed selected metadata accepted");
        }
        catch (InvalidDataException) { }
        var committed = current.RevisionId == first.RevisionId ? first : second;
        if (await AssetRevisionSeries.AppendOrVerifyAsync(directory, head, committed) != current ||
            (await AssetRevisionSeries.InspectAsync(directory)).Revisions.Count != 2)
            throw new Exception("retry duplicated the already-published revision");
        try
        {
            await AssetRevisionSeries.AppendOrVerifyAsync(directory, head, committed with { ProjectFileName = "different.ymmp" });
            throw new Exception("retry accepted different bytes at the same revision ID");
        }
        catch (InvalidDataException) { }
        try { await AssetRevisionSeries.AppendAsync(directory, head, second); throw new Exception("stale head accepted"); } catch (InvalidDataException) { }
        var idCollision = initial with { ParentRevisionId = current.RevisionId };
        try { await AssetRevisionSeries.AppendAsync(directory, current, idCollision); throw new Exception("historical revision collision accepted"); } catch (IOException) { }
        var incorrectProject = first with { RevisionId = Guid.NewGuid(), ParentRevisionId = current.RevisionId, ProjectId = Guid.NewGuid() };
        try { await AssetRevisionSeries.AppendAsync(directory, current, incorrectProject); throw new Exception("foreign project revision accepted"); } catch (InvalidDataException) { }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await AssetRevisionSeries.ReadHeadAsync(directory, cancelled.Token); throw new Exception("series cancellation ignored"); } catch (OperationCanceledException) { }
        }
        var foreign = initial with { RevisionId = Guid.NewGuid(), ParentRevisionId = initial.RevisionId };
        await AssetManifestStore.SaveNewAsync(Path.Combine(directory, $"revision-{foreign.RevisionId:N}.json"), foreign);
        try { await AssetRevisionSeries.ReadHeadAsync(directory); throw new Exception("divergent head accepted"); } catch (InvalidDataException) { }
        var graph = await AssetRevisionSeries.InspectGraphAsync(directory);
        var secondTip = graph.Heads.Single(tip => tip.RevisionId == foreign.RevisionId);
        if (graph.Root != head || graph.Revisions[0].Receipt != head || graph.Revisions.Count != 3 ||
            graph.Heads.Count != 2 || !graph.Heads.Contains(current))
            throw new Exception("divergent graph did not preserve both tips and their common root");
        var branch = await AssetRevisionSeries.ResolveBranchAsync(directory, head.ProjectId, head.RevisionId, head.Sha256,
            secondTip.RevisionId, secondTip.Sha256);
        if (branch.Selected.Receipt != secondTip || branch.Graph.Heads.Count != 2)
            throw new Exception("explicit branch selection was not resolved");
        var branchUpdate = AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, foreign, true, projectId: head.ProjectId);
        var newTip = await AssetRevisionSeries.AppendToBranchAsync(directory, head, graph.Heads, secondTip, branchUpdate);
        var updatedGraph = await AssetRevisionSeries.InspectGraphAsync(directory);
        if (!updatedGraph.Heads.ToHashSet().SetEquals([current, newTip]) || updatedGraph.Revisions.Count != 4)
            throw new Exception("extending one branch dropped or merged another branch");
        var concurrentUpdate = AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, committed, true, projectId: head.ProjectId);
        try
        {
            await AssetRevisionSeries.AppendToBranchAsync(directory, head, graph.Heads, current, concurrentUpdate);
            throw new Exception("branch append accepted a stale set of observed tips");
        }
        catch (InvalidDataException) { }
        try
        {
            await AssetRevisionSeries.AppendToBranchAsync(directory, head, updatedGraph.Heads, secondTip, branchUpdate);
            throw new Exception("branch append accepted an ambiguous historical selection");
        }
        catch (InvalidDataException) { }
        var firstAfterDivergence = await File.ReadAllBytesAsync(firstPath);
        if (!oldBytes.SequenceEqual(firstAfterDivergence)) throw new Exception("divergence changed historical revision");

        var corruptDirectory = Path.Combine(root, "corrupt-series"); Directory.CreateDirectory(corruptDirectory);
        var corruptRoot = await AssetRevisionSeries.InitializeAsync(corruptDirectory, initial);
        var corruptPath = Path.Combine(corruptDirectory, $"revision-{initial.RevisionId:N}.json");
        var altered = initial with { ProjectFileName = "altered.ymmp" };
        await File.WriteAllTextAsync(corruptPath, JsonSerializer.Serialize(altered));
        try
        {
            await AssetRevisionSeries.ResolveAsync(corruptDirectory, corruptRoot.ProjectId, corruptRoot.RevisionId, corruptRoot.Sha256, corruptRoot.RevisionId, corruptRoot.Sha256);
            throw new Exception("replaced metadata root accepted");
        }
        catch (InvalidDataException) { }
        try { await AssetRevisionSeries.AppendAsync(corruptDirectory, corruptRoot, first); throw new Exception("replaced parent accepted"); } catch (InvalidDataException) { }
        if ((await AssetManifestStore.LoadAsync(corruptPath)).ProjectFileName != "altered.ymmp") throw new Exception("replacement overwritten");

        var orphanDirectory = Path.Combine(root, "orphan-series"); Directory.CreateDirectory(orphanDirectory);
        await AssetManifestStore.SaveNewAsync(Path.Combine(orphanDirectory, $"revision-{first.RevisionId:N}.json"), first);
        try { await AssetRevisionSeries.ReadHeadAsync(orphanDirectory); throw new Exception("missing parent accepted"); } catch (InvalidDataException) { }

        var duplicateDirectory = Path.Combine(root, "duplicate-json-series"); Directory.CreateDirectory(duplicateDirectory);
        var duplicatePath = Path.Combine(duplicateDirectory, $"revision-{initial.RevisionId:N}.json");
        var duplicateJson = JsonSerializer.Serialize(initial).Insert(1, "\"SchemaVersion\":1,");
        await File.WriteAllTextAsync(duplicatePath, duplicateJson);
        try { await AssetRevisionSeries.ReadHeadAsync(duplicateDirectory); throw new Exception("duplicate JSON property accepted"); } catch (InvalidDataException) { }
        if (await File.ReadAllTextAsync(duplicatePath) != duplicateJson) throw new Exception("corrupt revision was altered");
    }
}
