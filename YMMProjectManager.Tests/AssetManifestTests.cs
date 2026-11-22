using System.Diagnostics;
using System.IO;
using System.Text.Json;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Relink;

//CSJ,class=AssetManifestTests,素材目録テスト
internal static class AssetManifestTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    public static async Task RunAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(workRoot, "asset-manifest"); Directory.CreateDirectory(root);
        //CSJ,variable=project,プロジェクト
        var project = Path.Combine(root, "project.ymmp");
        await File.WriteAllTextAsync(project, "{}"); await File.WriteAllTextAsync(Path.Combine(root, "asset.txt"), "asset");
        var item = await AssetInventory.InspectAsync(root, "asset.txt");
        await File.WriteAllTextAsync(Path.Combine(root, "second.txt"), "second");
        var second = await AssetInventory.InspectAsync(root, "second.txt");
        try { AssetManifestStore.Create(project, [item, item with { RelativePath = ".\\asset.txt" }], DateTimeOffset.UtcNow); throw new Exception("duplicate paths accepted"); } catch (InvalidDataException) { }
        //CSJ,variable=initial,初期
        var initial = AssetManifestStore.Create(project, [item, second], DateTimeOffset.UtcNow);
        //CSJ,variable=path,パス
        var path = Path.Combine(root, "manifest-1.json"); await AssetManifestStore.SaveNewAsync(path, initial);
        //CSJ,variable=loaded,読み込み済み
        var loaded = await AssetManifestStore.LoadAsync(path);
        //CSJ,variable=snapshot,スナップショット
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
        //CSJ,variable=identity,識別情報
        var identity = Guid.NewGuid();
        var associated = AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, projectId: identity);
        if (associated.ProjectId != identity) throw new Exception("project identity not associated");
        var renamed = AssetManifestStore.Create(Path.Combine(root, "renamed.ymmp"), [item], DateTimeOffset.UtcNow, associated, true, projectId: identity);
        if (renamed.ProjectId != identity || renamed.ParentRevisionId != associated.RevisionId) throw new Exception("rename lost identity");
        try { AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, associated, true, projectId: Guid.NewGuid()); throw new Exception("wrong project identity accepted"); } catch (InvalidDataException) { }
        try { AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, projectId: Guid.Empty); throw new Exception("empty identity accepted"); } catch (InvalidDataException) { }
        var candidate = Path.Combine(root, "candidate.txt"); await File.WriteAllTextAsync(candidate, "asset");
        var row = new RelinkRow { OriginalPath = "asset.txt", SelectedCandidate = candidate, Status = RelinkStatus.Updated };
        //CSJ,variable=matching,一致する
        var matching = await RelinkCandidateVerifier.CompareExpectedAsync([row], project, identity, associated, CancellationToken.None);
        if (!matching.Single().Contains("判定: 一致")) throw new Exception("expected hash match missed");
        await File.WriteAllTextAsync(candidate, "changed");
        var differing = await RelinkCandidateVerifier.CompareExpectedAsync([row], project, identity, associated, CancellationToken.None);
        if (!differing.Single().Contains("判定: 不一致")) throw new Exception("expected hash mismatch missed");
        await File.WriteAllTextAsync(candidate, "asset");
        var reverted = await RelinkCandidateVerifier.CompareExpectedAsync([row], project, identity, associated, CancellationToken.None);
        if (differing.SequenceEqual(reverted)) throw new Exception("candidate changed after displayed comparison without detection");
        //CSJ,variable=unknownRow,不明行
        var unknownRow = new RelinkRow { OriginalPath = "unrecorded.txt", SelectedCandidate = candidate, Status = RelinkStatus.Updated };
        if (!(await RelinkCandidateVerifier.CompareExpectedAsync([unknownRow], project, identity, associated, CancellationToken.None)).Single().Contains("期待ハッシュなし")) throw new Exception("missing expected hash misreported");
        try { await RelinkCandidateVerifier.CompareExpectedAsync([row], project, Guid.NewGuid(), associated, CancellationToken.None); throw new Exception("wrong identity accepted for comparison"); } catch (InvalidDataException) { }
        if (loaded.ProjectId != initial.ProjectId || loaded.Assets.Count != 2) throw new Exception("metadata read failed");
        try { AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, loaded); throw new Exception("unconfirmed association accepted"); } catch (InvalidDataException) { }
        var next = AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, loaded, confirmedProjectAssociation: true);
        if (next.ProjectId != initial.ProjectId || next.ParentRevisionId != initial.RevisionId || next.Assets.Count != 2 || !next.Assets.Contains(second)) throw new Exception("revision identity or retention failed");
        //CSJ,variable=changed,変更済み
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
        //CSJ,method=Save,保存する
        async Task<bool> Save(AssetManifest value) { try { await AssetManifestStore.SaveNewAsync(racePath, value); return true; } catch (IOException) { return false; } }
        var winners = await Task.WhenAll(Save(next), Save(AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, loaded, true)));
        if (winners.Count(x => x) != 1 || (await AssetManifestStore.LoadAsync(racePath)).ProjectId != initial.ProjectId) throw new Exception("concurrent revisions overwritten");
        await Program.RunNamedCaseAsync("Immutable metadata revisions and divergent legacy import",
            () => TestRevisionSeriesAsync(root, project, item));
        await Program.RunNamedCaseAsync("Managed relink branch selection, version compatibility and retry",
            () => TestRelinkRecoveryAsync(root));
        await Program.RunNamedCaseAsync("Metadata import preserves occupied destinations and unproven staging files",
            () => TestLegacyImportPublicationAsync(root));
        await Program.RunNamedCaseAsync("Metadata series resumes past abandoned staged revisions without accepting unknown entries",
            () => TestAbandonedRevisionStagingAsync(root));
        await Program.RunNamedCaseAsync("Managed relink recovery preserves an unpublished output when metadata advanced",
            () => TestStalePreparedOutputRecoveryAsync(root));
        await Program.RunNamedCaseAsync("Managed relink keeps its validated history stable through publication",
            () => TestHistoryPinnedForPublicationAsync(root, project, item));
        await Program.RunNamedCaseAsync("Managed relink durable output proof, cancellation and explicit retry",
            () => TestPreparedOutputRecoveryAsync(root));
        Console.WriteLine("Project asset immutable revision checks passed.");
    }

    private static async Task TestHistoryPinnedForPublicationAsync(string root, string project, AssetMetadata item)
    {
        //CSJ,variable=directory,ディレクトリ
        var directory = Directory.CreateDirectory(Path.Combine(root, "pinned-history")).FullName;
        var first = AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, projectId: Guid.NewGuid());
        //CSJ,variable=rootHead,ルートHEAD
        var rootHead = await AssetRevisionSeries.InitializeAsync(directory, first);
        var second = AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, first, true, projectId: first.ProjectId);
        var tip = await AssetRevisionSeries.AppendAsync(directory, rootHead, second);
        var next = AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, second, true, projectId: first.ProjectId);
        //CSJ,variable=paths,パス一覧
        var paths = new[] { first.RevisionId, second.RevisionId }
            .Select(id => Path.Combine(directory, $"revision-{id:N}.json")).ToArray();
        var original = await Task.WhenAll(paths.Select(path => File.ReadAllBytesAsync(path)));
        var checkpoints = 0;

        void CheckHistorySharing()
        {
            checkpoints++;
            AssertDirectoryPinned(directory);
            //CSJ,variable=path,パス
            foreach (var path in paths)
            {
                try { using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read); }
                catch (IOException) { continue; }
                throw new Exception("a validated revision was writable during output publication");
            }
        }

        //CSJ,variable=published,確定済み
        var published = await AssetRevisionSeries.AppendToBranchOrVerifyAsync(directory, rootHead, [tip], tip, next, default, CheckHistorySharing);
        var replayed = await AssetRevisionSeries.AppendToBranchOrVerifyAsync(directory, rootHead, [tip], tip, next, default, CheckHistorySharing);
        //CSJ,variable=graph,グラフ
        var graph = await AssetRevisionSeries.InspectGraphAsync(directory);
        if (checkpoints != 2 || published.RevisionId != next.RevisionId || replayed != published ||
            graph.Revisions.Count != 3 || !graph.Heads.SequenceEqual(new[] { published }))
            throw new Exception("guarded publication or committed retry did not leave one verified new revision");
        for (var i = 0; i < paths.Length; i++)
        {
            var after = await File.ReadAllBytesAsync(paths[i]);
            if (!original[i].AsSpan().SequenceEqual(after))
                throw new Exception("historical revision bytes changed");
            //CSJ,variable=writer,書き込み処理
            using var writer = new FileStream(paths[i], FileMode.Open, FileAccess.Write, FileShare.Read);
        }
        using (OwnedFilePublication.OpenOrdinaryPath(directory, expectDirectory: true, allowRename: true,
            FileShare.ReadWrite | FileShare.Delete)) { }
    }

    //CSJ,method=AssertDirectoryPinned,ディレクトリ保持済みを検査する
    private static void AssertDirectoryPinned(string directory)
    {
        try
        {
            //CSJ,variable=directoryWrite,ディレクトリ書き込み
            using var directoryWrite = OwnedFilePublication.OpenOrdinaryPath(directory, expectDirectory: true, allowRename: true,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch (IOException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 32 })
        {
            return;
        }
        throw new Exception("the operation did not hold the verified directory path");
    }

    //CSJ,method=TestRelinkRecoveryAsync,再リンク復旧をテストする（非同期）
    private static async Task TestRelinkRecoveryAsync(string root)
    {
        //CSJ,variable=folder,フォルダー
        var folder = Path.Combine(root, "managed-relink");
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "source.ymmp");
        var output = Path.Combine(folder, "output.ymmp");
        var original = Path.Combine(folder, "original.wav");
        var candidate = Path.Combine(folder, "candidate.wav");
        //CSJ,variable=seriesDirectory,系列ディレクトリ
        var seriesDirectory = Path.Combine(folder, "series");
        Directory.CreateDirectory(seriesDirectory);
        await File.WriteAllTextAsync(source, """{"Items":[{"FilePath":"original.wav"}]}""");
        await File.WriteAllTextAsync(original, "original asset");
        await File.WriteAllTextAsync(candidate, "replacement asset");
        //CSJ,variable=bytes,バイト列
        var bytes = await File.ReadAllBytesAsync(source);
        //CSJ,variable=originalAsset,元の素材
        var originalAsset = await AssetInventory.InspectAsync(folder, "original.wav");
        //CSJ,variable=initial,初期
        var initial = AssetManifestStore.Create(source, [originalAsset], DateTimeOffset.UtcNow);
        var head = await AssetRevisionSeries.InitializeAsync(seriesDirectory, initial);
        var context = new RelinkDocumentContext
        {
            YmmpPath = source, OriginalBytes = bytes,
            Rows = [new RelinkRow { OriginalPath = "original.wav", SelectedCandidate = candidate, Status = RelinkStatus.Updated }]
        };
        //CSJ,variable=review,レビュー
        var review = await AssetRelinkRecovery.ReviewAsync(context, seriesDirectory, head, head, default);
        if (review.Count != 1 || !review[0].RequiresConfirmation) throw new Exception("replacement hash mismatch was not displayed");
        await RejectAsync(() => AssetRelinkRecovery.PrepareAsync(context, output, seriesDirectory, head, head, review, false, default));
        await File.WriteAllTextAsync(candidate, "another asset");
        await RejectAsync(() => AssetRelinkRecovery.PrepareAsync(context, output, seriesDirectory, head, head, review, true, default));
        await File.WriteAllTextAsync(candidate, "replacement asset");
        //CSJ,variable=evidence,証跡
        var evidence = await AssetRelinkRecovery.PrepareAsync(context, output, seriesDirectory, head, head, review, true, default);
        //CSJ,variable=oldEvidence,旧証跡
        var oldEvidence = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(evidence))!.AsObject();
        oldEvidence["Version"] = 1;
        oldEvidence.Remove("ObservedTips");
        oldEvidence.Remove("SourceIdentity");
        await File.WriteAllTextAsync(evidence, oldEvidence.ToJsonString());
        try
        {
            await AssetRelinkRecovery.CompleteAsync(evidence, seriesDirectory, head, default);
            throw new Exception("revision written before output publication");
        }
        catch (FileNotFoundException) { }
        //CSJ,variable=cancelled,キャンセル済み
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
        //CSJ,variable=published,確定済み
        var published = await File.ReadAllBytesAsync(output);
        await File.WriteAllTextAsync(candidate, "another asset");
        await RejectAsync(() => AssetRelinkRecovery.CompleteAsync(evidence, seriesDirectory, head, default));
        await File.WriteAllTextAsync(candidate, "replacement asset");
        await File.WriteAllTextAsync(output, "{}");
        await RejectAsync(() => AssetRelinkRecovery.CompleteAsync(evidence, seriesDirectory, head, default));
        await File.WriteAllBytesAsync(output, published);
        if (await AssetRevisionSeries.ReadHeadAsync(seriesDirectory) != head) throw new Exception("a failed relink changed the metadata head");
        //CSJ,variable=transfer,受け渡し
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
        //CSJ,variable=persisted,永続化済み
        var persisted = false;
        await AssetRelinkRecovery.WithVerifiedProjectsAsync(transfer, _ =>
        {
            AssertDirectoryPinned(folder);
            //CSJ,variable=path,パス
            foreach (var path in new[] { source, output })
            {
                try
                {
                    //CSJ,variable=writer,書き込み処理
                    using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
                    throw new Exception("the transfer let another writer open a verified file");
                }
                catch (IOException) { }
            }
            persisted = true;
            return Task.CompletedTask;
        }, default);
        if (!persisted) throw new Exception("association transfer did not run with valid files");
        using (OwnedFilePublication.OpenOrdinaryPath(folder, expectDirectory: true, allowRename: true,
            FileShare.ReadWrite | FileShare.Delete)) { }

        // This operation treats equally hashed bytes in a different physical file as a replacement.
        //CSJ,variable=path,パス
        foreach (var path in new[] { source, output })
            await WithContentIdenticalReplacementAsync(path, () => RejectAsync(() =>
                AssetRelinkRecovery.WithVerifiedProjectsAsync(transfer,
                    _ => throw new Exception("association transfer ran with a replaced file"), default)));
        //CSJ,variable=rootFile,ルートファイル
        var rootFile = Path.Combine(seriesDirectory, $"revision-{head.RevisionId:N}.json");
        //CSJ,variable=rootBytes,ルートバイト列
        var rootBytes = await File.ReadAllBytesAsync(rootFile);
        await File.AppendAllTextAsync(rootFile, " ");
        var observed = await AssetRevisionSeries.InspectAsync(seriesDirectory);
        if (observed.Head != completed || observed.Root == head)
            throw new Exception("the fixture must change only the root receipt");
        //CSJ,variable=uncommitted,未コミット
        var uncommitted = AssetManifestStore.Create(output, [], DateTimeOffset.UtcNow,
            observed.Revisions[^1].Manifest, true, projectId: head.ProjectId);
        await RejectAsync(() => AssetRevisionSeries.AppendAsync(seriesDirectory, head, completed, uncommitted));
        await RejectAsync(() => AssetRelinkRecovery.CompleteAsync(evidence, seriesDirectory, head, default));
        await File.WriteAllBytesAsync(rootFile, rootBytes);
        //CSJ,variable=pendingOutput,保留出力
        var pendingOutput = Path.Combine(folder, "pending.ymmp");
        //CSJ,variable=pendingReview,保留レビュー
        var pendingReview = await AssetRelinkRecovery.ReviewAsync(context, seriesDirectory, head, completed, default);
        //CSJ,variable=pending,保留
        var pending = await AssetRelinkRecovery.PrepareAsync(context, pendingOutput, seriesDirectory, head, completed, pendingReview, true, default);
        if (!(await saver.SaveAsAsync(context, pendingOutput, default)).Success) throw new Exception("pending relink SaveAs failed");
        var competing = AssetManifestStore.Create(output, [], DateTimeOffset.UtcNow,
            (await AssetRevisionSeries.InspectAsync(seriesDirectory)).Revisions[^1].Manifest, true, projectId: head.ProjectId);
        var competingHead = await AssetRevisionSeries.AppendOrVerifyAsync(seriesDirectory, completed, competing);
        await RejectAsync(() => AssetRelinkRecovery.CompleteAsync(pending, seriesDirectory, head, default));
        //CSJ,variable=pendingBytes,保留バイト列
        var pendingBytes = await File.ReadAllBytesAsync(pendingOutput);
        //CSJ,variable=history,履歴
        var history = await AssetRevisionSeries.InspectAsync(seriesDirectory);
        var preservedSource = await File.ReadAllBytesAsync(source);
        var preservedOutput = await File.ReadAllBytesAsync(output);
        if (repeated != completed || history.Head != competingHead || history.Revisions.Count != 3 ||
            history.Revisions[1].Manifest.Assets.Count != 2 || !bytes.SequenceEqual(preservedSource) ||
            !published.SequenceEqual(preservedOutput) || !published.SequenceEqual(pendingBytes) || !File.Exists(pending))
            throw new Exception("relink recovery duplicated a revision or failed to preserve existing or pending data");

        var fork = AssetManifestStore.Create(source, [originalAsset], DateTimeOffset.UtcNow, initial, true, projectId: head.ProjectId);
        await AssetManifestStore.SaveNewAsync(Path.Combine(seriesDirectory, $"revision-{fork.RevisionId:N}.json"), fork);
        //CSJ,variable=graph,グラフ
        var graph = await AssetRevisionSeries.InspectGraphAsync(seriesDirectory);
        if (await AssetRelinkRecovery.CompleteForTransferAsync(evidence, seriesDirectory, head, default, source) != transfer)
            throw new Exception("A later fork prevented proof of an already committed legacy relink.");
        //CSJ,variable=legacyPending,旧形式保留
        var legacyPending = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(pending))!.AsObject();
        legacyPending["Version"] = 1;
        legacyPending.Remove("ObservedTips");
        legacyPending.Remove("SourceIdentity");
        await File.WriteAllTextAsync(pending, legacyPending.ToJsonString());
        await RejectAsync(() => AssetRelinkRecovery.CompleteAsync(pending, seriesDirectory, head, default));
        var forkTip = graph.Heads.Single(tip => tip.RevisionId == fork.RevisionId);
        await RejectAsync(() => AssetRelinkRecovery.ReviewAsync(context, seriesDirectory, head, forkTip, default));
        await RejectAsync(() => AssetRelinkRecovery.ReviewAsync(context, seriesDirectory, head, head, graph.Heads, default));
        var forkReview = await AssetRelinkRecovery.ReviewAsync(context, seriesDirectory, head, forkTip, graph.Heads, default);
        var forkOutput = Path.Combine(folder, "fork.ymmp");
        var forkEvidence = await AssetRelinkRecovery.PrepareAsync(context, forkOutput, seriesDirectory, head, forkTip, graph.Heads, forkReview, true, default);
        if (!(await saver.SaveAsAsync(context, forkOutput, default)).Success) throw new Exception("branch relink SaveAs failed");
        var forkIntentBytes = await File.ReadAllBytesAsync(forkEvidence);
        await WithContentIdenticalReplacementAsync(source, () => RejectAsync(() =>
            AssetRelinkRecovery.CompleteForTransferAsync(forkEvidence, seriesDirectory, head, default, source)));
        var corruptIntent = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(forkEvidence))!.AsObject();
        corruptIntent.Remove("ObservedTips");
        await File.WriteAllTextAsync(forkEvidence, corruptIntent.ToJsonString());
        await RejectAsync(() => AssetRelinkRecovery.CompleteForTransferAsync(forkEvidence, seriesDirectory, head, default, source));
        if (!(await AssetRevisionSeries.InspectGraphAsync(seriesDirectory)).Heads.SequenceEqual(graph.Heads))
            throw new Exception("corrupt branch evidence changed the series");
        await File.WriteAllBytesAsync(forkEvidence, forkIntentBytes);
        //CSJ,variable=branchReceipt,ブランチ照合記録
        var branchReceipt = await AssetRelinkRecovery.CompleteForTransferAsync(forkEvidence, seriesDirectory, head, default, source);
        var afterFork = await AssetRevisionSeries.InspectGraphAsync(seriesDirectory);
        if (!afterFork.Heads.ToHashSet().SetEquals([competingHead, branchReceipt.Revision]) ||
            afterFork.Revisions.Single(revision => revision.Receipt == branchReceipt.Revision).Manifest.ParentRevisionId != forkTip.RevisionId)
            throw new Exception("managed relink updated the wrong metadata branch");

        var staleOutput = Path.Combine(folder, "stale.ymmp");
        var staleReview = await AssetRelinkRecovery.ReviewAsync(context, seriesDirectory, head, branchReceipt.Revision, afterFork.Heads, default);
        var staleEvidence = await AssetRelinkRecovery.PrepareAsync(context, staleOutput, seriesDirectory, head, branchReceipt.Revision, afterFork.Heads, staleReview, true, default);
        if (!(await saver.SaveAsAsync(context, staleOutput, default)).Success) throw new Exception("stale branch relink SaveAs failed");
        var otherUpdate = AssetManifestStore.Create(output, [], DateTimeOffset.UtcNow, competing, true, projectId: head.ProjectId);
        var otherTip = await AssetRevisionSeries.AppendToBranchAsync(seriesDirectory, head, afterFork.Heads, competingHead, otherUpdate);
        await RejectAsync(() => AssetRelinkRecovery.CompleteForTransferAsync(staleEvidence, seriesDirectory, head, default, source));
        if (!File.Exists(staleOutput) || !File.Exists(staleEvidence)) throw new Exception("a stale branch lost its output or intent");
        if (await AssetRelinkRecovery.CompleteForTransferAsync(forkEvidence, seriesDirectory, head, default, source) != branchReceipt)
            throw new Exception("a committed branch relink could not be confirmed after another branch advanced");
        //CSJ,variable=finalGraph,最終グラフ
        var finalGraph = await AssetRevisionSeries.InspectGraphAsync(seriesDirectory);
        if (!finalGraph.Heads.ToHashSet().SetEquals([otherTip, branchReceipt.Revision]) || finalGraph.Revisions.Count != 6)
            throw new Exception("a refused or repeated branch completion published an unexpected revision");

        static async Task WithContentIdenticalReplacementAsync(string path, Func<Task> verify)
        {
            var original = path + ".preserved";
            File.Move(path, original);
            File.Copy(original, path);
            try { await verify(); }
            finally
            {
                File.Delete(path);
                File.Move(original, path);
            }
        }

        //CSJ,method=RejectAsync,拒否する（非同期）
        static async Task RejectAsync(Func<Task> run)
        {
            try { await run(); throw new Exception("invalid relink state accepted"); }
            catch (InvalidDataException) { }
        }
    }

    //CSJ,method=TestLegacyImportPublicationAsync,旧形式取り込み確定反映をテストする（非同期）
    private static async Task TestLegacyImportPublicationAsync(string parent)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(parent, "legacy-publication");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "original.ymmp");
        await File.WriteAllTextAsync(source, "{}");
        //CSJ,variable=manifest,目録
        var manifest = AssetManifestStore.Create(source, [], DateTimeOffset.UtcNow);
        //CSJ,variable=legacy,旧形式
        var legacy = Path.Combine(root, "legacy.json");
        await AssetManifestStore.SaveNewAsync(legacy, manifest);
        var original = await File.ReadAllBytesAsync(legacy);
        var occupied = Path.Combine(root, "occupied");
        var sentinel = Path.Combine(occupied, "keep.txt");
        string? retained = null;
        try
        {
            await AssetRevisionSeries.ImportLegacyGraphAsync(occupied, manifest.ProjectId, [legacy], default, _ =>
            {
                Directory.CreateDirectory(occupied);
                File.WriteAllText(sentinel, "external");
            });
            throw new Exception("Metadata import replaced an occupied destination.");
        }
        catch (IOException ex) { retained = ex.Data[AssetRevisionSeries.RetainedImportPath] as string; }
        //CSJ,variable=name,名前
        var name = $"revision-{manifest.RevisionId:N}.json";
        if (await File.ReadAllTextAsync(sentinel) != "external" || retained is null ||
            File.Exists(Path.Combine(occupied, name)))
            throw new Exception("A destination conflict lost external contents or the prepared history.");
        var retainedBytes = await File.ReadAllBytesAsync(Path.Combine(retained, name));
        if (!original.SequenceEqual(retainedBytes))
            throw new Exception("The independently retained original revision was modified.");

        var edited = Path.Combine(root, "edited");
        retained = null;
        try
        {
            await AssetRevisionSeries.ImportLegacyGraphAsync(edited, manifest.ProjectId, [legacy], default,
                //CSJ,parameter=temporary,一時
                temporary => File.WriteAllText(Path.Combine(temporary, "unrelated.txt"), "retain"));
            throw new Exception("Metadata import published unexpected staging contents.");
        }
        catch (InvalidDataException ex) { retained = ex.Data[AssetRevisionSeries.RetainedImportPath] as string; }
        if (Directory.Exists(edited) || retained is null || await File.ReadAllTextAsync(Path.Combine(retained, "unrelated.txt")) != "retain")
            throw new Exception("Refused import lost an unproven staging file.");

        //CSJ,variable=retry,再試行
        var retry = Path.Combine(root, "retried");
        var completed = await AssetRevisionSeries.ImportLegacyGraphAsync(retry, manifest.ProjectId, [legacy]);
        //CSJ,variable=published,確定済み
        var published = await File.ReadAllBytesAsync(Path.Combine(retry, name));
        //CSJ,variable=sourceAfter,元変更後
        var sourceAfter = await File.ReadAllBytesAsync(legacy);
        if (completed.Root.RevisionId != manifest.RevisionId || !original.SequenceEqual(published) || !original.SequenceEqual(sourceAfter))
            throw new Exception("A new import could not publish the exact legacy history.");
    }

    private static async Task TestAbandonedRevisionStagingAsync(string parent)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(parent, "abandoned-revision-staging");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "sample.ymmp");
        await File.WriteAllTextAsync(source, "{}");
        //CSJ,variable=manifest,目録
        var manifest = AssetManifestStore.Create(source, [], DateTimeOffset.UtcNow);
        var head = await AssetRevisionSeries.InitializeAsync(root, manifest);
        var next = AssetManifestStore.Create(source, [], DateTimeOffset.UtcNow, manifest, confirmedProjectAssociation: true);
        //CSJ,variable=pending,保留
        var pending = Path.Combine(root, $"revision-{next.RevisionId:N}.json.tmp-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(pending, """{"Partial":""");
        if (await AssetRevisionSeries.ReadHeadAsync(root) != head)
            throw new Exception("A legacy staging record changed the committed head.");

        var appended = await AssetRevisionSeries.AppendAsync(root, head, next);
        if (await AssetRevisionSeries.ReadHeadAsync(root) != appended ||
            await File.ReadAllTextAsync(pending) != """{"Partial":""")
            throw new Exception("Publication did not preserve the abandoned legacy stage.");

        //CSJ,variable=unknown,不明
        var unknown = Path.Combine(root, $"revision-{Guid.NewGuid():N}.json.tmp-unknown");
        await File.WriteAllTextAsync(unknown, "{}");
        try
        {
            await AssetRevisionSeries.ReadHeadAsync(root);
            throw new Exception("An unrecognized history entry was interpreted as staging.");
        }
        catch (InvalidDataException) { }
        if (await File.ReadAllTextAsync(unknown) != "{}")
            throw new Exception("Inspection changed an unrecognized entry.");
    }

    private static async Task TestStalePreparedOutputRecoveryAsync(string parent)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(parent, "stale-prepared-output");
        //CSJ,variable=series,系列
        var series = Path.Combine(root, "series");
        Directory.CreateDirectory(series);
        var source = Path.Combine(root, "source.ymmp");
        var output = Path.Combine(root, "recovered.ymmp");
        var candidate = Path.Combine(root, "candidate.wav");
        var original = """{"Items":[{"FilePath":"original.wav"}]}"""u8.ToArray();
        await File.WriteAllBytesAsync(source, original);
        await File.WriteAllTextAsync(candidate, "synthetic candidate");
        //CSJ,variable=baseRevision,基点版
        var baseRevision = AssetManifestStore.Create(source, [], DateTimeOffset.UtcNow);
        //CSJ,variable=baseline,基準
        var baseline = await AssetRevisionSeries.InitializeAsync(series, baseRevision);
        var context = new RelinkDocumentContext
        {
            YmmpPath = source,
            OriginalBytes = original,
            Rows = [new RelinkRow { OriginalPath = "original.wav", SelectedCandidate = candidate, Status = RelinkStatus.Updated }],
        };
        //CSJ,variable=review,レビュー
        var review = await AssetRelinkRecovery.ReviewAsync(context, series, baseline, baseline, default);
        //CSJ,variable=evidence,証跡
        var evidence = await AssetRelinkRecovery.PrepareManagedOutputAsync(
            context, output, series, baseline, baseline, [baseline], review, true, default);
        var staged = evidence + ".output.tmp";
        //CSJ,variable=proof,確認証跡
        var proof = evidence + ".publish";
        //CSJ,variable=logger,ロガー
        var logger = new FileLogger(Path.Combine(root, "test.log"));
        using (var stopAfterProof = new CancellationTokenSource())
        {
            //CSJ,variable=interrupted,中断済み
            var interrupted = new RelinkSaveService(logger, null, null, stopAfterProof.Cancel);
            try
            {
                await AssetRelinkRecovery.StagePreparedOutputAsync(evidence, context, interrupted, stopAfterProof.Token);
                throw new Exception("Staged publication did not reach the interruption point.");
            }
            catch (OperationCanceledException) when (stopAfterProof.IsCancellationRequested) { }
        }
        //CSJ,variable=stagedBytes,準備済みバイト列
        var stagedBytes = await File.ReadAllBytesAsync(staged);
        //CSJ,variable=proofBytes,確認証跡バイト列
        var proofBytes = await File.ReadAllBytesAsync(proof);
        await using (var occupied = new FileStream(Path.Combine(series, ".asset-revisions.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try
            {
                await AssetRelinkRecovery.CompleteForTransferAsync(evidence, series, baseline, default, source, resumePreparedOutput: true);
                throw new Exception("An occupied metadata writer lock was ignored.");
            }
            catch (IOException) { }
            if (File.Exists(output) || !File.Exists(staged))
                throw new Exception("A busy metadata series consumed the prepared output.");
        }
        var followUp = AssetManifestStore.Create(source, [], DateTimeOffset.UtcNow, baseRevision, confirmedProjectAssociation: true);
        var competing = await AssetRevisionSeries.AppendAsync(series, baseline, followUp);

        //CSJ,variable=attempt,試行
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await AssetRelinkRecovery.CompleteForTransferAsync(evidence, series, baseline, default, source, resumePreparedOutput: true);
                throw new Exception("A stale metadata branch was accepted for recovery.");
            }
            catch (InvalidDataException) { }
            if (File.Exists(output)) throw new Exception("A stale metadata branch caused recovery to publish the prepared output.");
            //CSJ,variable=stagedAfter,準備済み変更後
            var stagedAfter = await File.ReadAllBytesAsync(staged);
            //CSJ,variable=proofAfter,確認証跡変更後
            var proofAfter = await File.ReadAllBytesAsync(proof);
            //CSJ,variable=sourceAfter,元変更後
            var sourceAfter = await File.ReadAllBytesAsync(source);
            if (!stagedBytes.SequenceEqual(stagedAfter) || !proofBytes.SequenceEqual(proofAfter) ||
                !original.SequenceEqual(sourceAfter) || await AssetRevisionSeries.ReadHeadAsync(series) != competing)
                throw new Exception("A refused recovery lost or changed an original or its evidence.");
        }
    }

    //CSJ,method=TestPreparedOutputRecoveryAsync,準備済み出力復旧をテストする（非同期）
    private static async Task TestPreparedOutputRecoveryAsync(string parent)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(parent, "prepared-output");
        //CSJ,variable=series,系列
        var series = Path.Combine(root, "series");
        Directory.CreateDirectory(series);
        var source = Path.Combine(root, "source.ymmp");
        var candidate = Path.Combine(root, "candidate.wav");
        var original = """{"Items":[{"FilePath":"original.wav"}]}"""u8.ToArray();
        await File.WriteAllBytesAsync(source, original);
        await File.WriteAllTextAsync(candidate, "synthetic candidate");
        //CSJ,variable=manifest,目録
        var manifest = AssetManifestStore.Create(source, [], DateTimeOffset.UtcNow);
        //CSJ,variable=baseline,基準
        var baseline = await AssetRevisionSeries.InitializeAsync(series, manifest);
        //CSJ,variable=logger,ロガー
        var logger = new FileLogger(Path.Combine(root, "test.log"));
        var context = new RelinkDocumentContext
        {
            YmmpPath = source, OriginalBytes = original,
            Rows = [new RelinkRow { OriginalPath = "original.wav", SelectedCandidate = candidate, Status = RelinkStatus.Updated }]
        };

        //CSJ,method=PrepareAsync,準備する（非同期）
        async Task<string> PrepareAsync(string path, AssetRevisionSeries.Head tip)
        {
            //CSJ,variable=review,レビュー
            var review = await AssetRelinkRecovery.ReviewAsync(context, series, baseline, tip, [tip], default);
            return await AssetRelinkRecovery.PrepareManagedOutputAsync(context, path, series, baseline, tip, [tip], review, true, default);
        }

        //CSJ,method=RejectAsync,拒否する（非同期）
        static async Task RejectAsync<T>(Func<Task> operation) where T : Exception
        {
            try { await operation(); }
            catch (T) { return; }
            throw new Exception("Invalid managed relink operation was accepted.");
        }

        var unrelated = Path.Combine(root, "unproven.ymmp");
        var unproven = await PrepareAsync(unrelated, baseline);
        //CSJ,variable=partialWriter,部分書き込み処理
        var partialWriter = new RelinkSaveService(logger, async (stream, bytes, token) =>
        {
            await stream.WriteAsync(bytes.AsMemory(0, bytes.Length / 2), token);
            throw new IOException("synthetic incomplete staged write");
        }, null, null);
        if ((await AssetRelinkRecovery.StagePreparedOutputAsync(unproven, context, partialWriter, default)).Success ||
            File.Exists(unrelated) || File.Exists(unproven + ".output.tmp") || File.Exists(unproven + ".publish"))
            throw new Exception("Failed pre-proof save published or leaked a stage.");
        await File.WriteAllBytesAsync(unrelated, RelinkJsonUpdate.Apply(original, context.Rows.ToList()));
        await RejectAsync<FileNotFoundException>(() =>
            AssetRelinkRecovery.CompleteForTransferAsync(unproven, series, baseline, default, source, resumePreparedOutput: true));

        var output = Path.Combine(root, "retry.ymmp");
        //CSJ,variable=evidence,証跡
        var evidence = await PrepareAsync(output, baseline);
        var staged = evidence + ".output.tmp";
        //CSJ,variable=proof,確認証跡
        var proof = evidence + ".publish";
        using (var stopAfterProof = new CancellationTokenSource())
        {
            //CSJ,variable=interrupted,中断済み
            var interrupted = new RelinkSaveService(logger, null, null, stopAfterProof.Cancel);
            await RejectAsync<OperationCanceledException>(() =>
                AssetRelinkRecovery.StagePreparedOutputAsync(evidence, context, interrupted, stopAfterProof.Token));
        }
        if (File.Exists(output) || !File.Exists(staged) || !File.Exists(proof) ||
            await AssetRevisionSeries.ReadHeadAsync(series) != baseline)
            throw new Exception("Interruption between proof and rename did not preserve a retriable stage.");
        //CSJ,variable=stagedBytes,準備済みバイト列
        var stagedBytes = await File.ReadAllBytesAsync(staged);
        //CSJ,variable=stagedIdentity,準備済み識別情報
        var stagedIdentity = await ProjectFileEvidence.ReadAsync(staged);
        //CSJ,variable=proofBytes,確認証跡バイト列
        var proofBytes = await File.ReadAllBytesAsync(proof);
        await RejectAsync<FileNotFoundException>(() => AssetRelinkRecovery.CompleteForTransferAsync(evidence, series, baseline, default, source));

        //CSJ,variable=missing,欠落
        foreach (var missing in new[] { source, candidate })
        {
            // Simulate a temporarily unavailable original without creating a replacement identity.
            var retained = missing + ".temporarily-unavailable";
            File.Move(missing, retained);
            try
            {
                await RejectAsync<FileNotFoundException>(() =>
                    AssetRelinkRecovery.CompleteForTransferAsync(evidence, series, baseline, default, source, resumePreparedOutput: true));
                //CSJ,variable=stageAfter,作業段階変更後
                var stageAfter = await File.ReadAllBytesAsync(staged);
                //CSJ,variable=proofAfter,確認証跡変更後
                var proofAfter = await File.ReadAllBytesAsync(proof);
                if (File.Exists(output) || !stageAfter.SequenceEqual(stagedBytes) || !proofAfter.SequenceEqual(proofBytes) ||
                    await AssetRevisionSeries.ReadHeadAsync(series) != baseline)
                    throw new Exception("An unavailable original caused recovery to consume evidence or publish output.");
            }
            finally { File.Move(retained, missing); }
        }

        // A different file at the proposed destination must not be consumed, even when the bytes match.
        await File.WriteAllBytesAsync(output, stagedBytes);
        await RejectAsync<InvalidDataException>(() =>
            AssetRelinkRecovery.CompleteForTransferAsync(evidence, series, baseline, default, source, resumePreparedOutput: true));
        var keptCollision = await File.ReadAllBytesAsync(output);
        if (!stagedBytes.SequenceEqual(keptCollision) || !File.Exists(staged) || await AssetRevisionSeries.ReadHeadAsync(series) != baseline)
            throw new Exception("The occupied destination or prepared stage was changed.");
        File.Delete(output);

        // Even with the same file identity, changed staged bytes must stop recovery.
        await File.WriteAllTextAsync(staged, "unexpected staged contents");
        await RejectAsync<InvalidDataException>(() =>
            AssetRelinkRecovery.CompleteForTransferAsync(evidence, series, baseline, default, source, resumePreparedOutput: true));
        if (File.Exists(output)) throw new Exception("Recovery published a changed stage.");
        await File.WriteAllBytesAsync(staged, stagedBytes);

        var recovered = await AssetRelinkRecovery.CompleteForTransferAsync(evidence, series, baseline, default, source, resumePreparedOutput: true);
        var again = await AssetRelinkRecovery.CompleteForTransferAsync(evidence, series, baseline, default, source, resumePreparedOutput: true);
        if (again != recovered || File.Exists(staged) || !File.Exists(proof) ||
            !stagedIdentity.IsSameFile(await ProjectFileEvidence.ReadAsync(output)) ||
            (await AssetRevisionSeries.InspectGraphAsync(series)).Revisions.Count != 2)
            throw new Exception("Explicit retry did not publish the owned stage exactly once.");

        var second = Path.Combine(root, "normal.ymmp");
        //CSJ,variable=secondEvidence,2番目証跡
        var secondEvidence = await PrepareAsync(second, recovered.Revision);
        if (!(await AssetRelinkRecovery.StagePreparedOutputAsync(secondEvidence, context, new RelinkSaveService(logger), default)).Success)
            throw new Exception("Regular managed staging failed.");
        if (File.Exists(second) || !File.Exists(secondEvidence + ".output.tmp") ||
            await AssetRevisionSeries.ReadHeadAsync(series) != recovered.Revision)
            throw new Exception("Regular managed save published before locked metadata validation.");
        var receiptBeforeReplacement = await AssetRelinkRecovery.CompleteForTransferAsync(secondEvidence, series, baseline, default, source,
            resumePreparedOutput: true);
        //CSJ,variable=secondBytes,2番目バイト列
        var secondBytes = await File.ReadAllBytesAsync(second);
        var preserved = second + ".retained";
        File.Move(second, preserved);
        await File.WriteAllBytesAsync(second, secondBytes);
        await RejectAsync<InvalidDataException>(() =>
            AssetRelinkRecovery.CompleteForTransferAsync(secondEvidence, series, baseline, default, source));
        var stillOriginal = await File.ReadAllBytesAsync(source);
        if ((await AssetRevisionSeries.InspectGraphAsync(series)).Revisions.Count != 3 ||
            !original.SequenceEqual(stillOriginal))
            throw new Exception("Replaced output altered source or metadata history.");
        File.Delete(second);
        File.Move(preserved, second);
        //CSJ,variable=secondReceipt,2番目照合記録
        var secondReceipt = await AssetRelinkRecovery.CompleteForTransferAsync(secondEvidence, series, baseline, default, source);
        if (secondReceipt != receiptBeforeReplacement)
            throw new Exception("Recovered already-published output no longer matches its original receipt.");

        var crashOutput = Path.Combine(root, "crash.ymmp");
        var crashEvidence = await PrepareAsync(crashOutput, secondReceipt.Revision);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--r16-relink-publication-child");
        start.ArgumentList.Add(crashEvidence);
        // An absent child dispatch must fail the fixture immediately.
        start.Environment["YMM_TEST_FILTER"] = "__r16-relink-publication-child-only__";
        using (var child = Process.Start(start) ?? throw new Exception("Relink crash fixture could not start."))
        {
            //CSJ,variable=timeout,タイムアウト
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await child.WaitForExitAsync(timeout.Token); }
            finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
            if (child.ExitCode != 87) throw new Exception("Relink child missed the durable publication boundary.");
        }
        if (File.Exists(crashOutput) || !File.Exists(crashEvidence + ".output.tmp") || !File.Exists(crashEvidence + ".publish") ||
            await AssetRevisionSeries.ReadHeadAsync(series) != secondReceipt.Revision)
            throw new Exception("Child exit after proof changed the destination or lost the prepared stage.");
        var afterCrash = await AssetRelinkRecovery.CompleteForTransferAsync(crashEvidence, series, baseline, default, source, resumePreparedOutput: true);
        if (!File.Exists(crashOutput) || afterCrash.Output != crashOutput || File.Exists(crashEvidence + ".output.tmp") ||
            (await AssetRevisionSeries.InspectGraphAsync(series)).Revisions.Count != 4)
            throw new Exception("Interrupted owned output was not published exactly once after child exit.");
    }

    internal static async Task InterruptPreparedOutputAsync(string evidence)
    {
        evidence = Path.GetFullPath(evidence);
        //CSJ,variable=allowed,許可済み
        var allowed = Path.Combine(Path.GetTempPath(), "YMMProjectManager-tests") + Path.DirectorySeparatorChar;
        //CSJ,variable=root,ルート
        var root = Path.GetDirectoryName(evidence)!;
        if (!evidence.StartsWith(Path.GetFullPath(allowed), StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(root) != "prepared-output" ||
            !Path.GetFileName(evidence).StartsWith(".ymmpm-relink-", StringComparison.Ordinal))
            throw new InvalidOperationException("Relink child fixture must be isolated.");
        ProjectListDrafts.CheckOrdinaryDirectory(root);
        var source = Path.Combine(root, "source.ymmp");
        var context = new RelinkDocumentContext
        {
            YmmpPath = source,
            OriginalBytes = await File.ReadAllBytesAsync(source),
            Rows = [new RelinkRow
            {
                OriginalPath = "original.wav",
                SelectedCandidate = Path.Combine(root, "candidate.wav"),
                Status = RelinkStatus.Updated,
            }],
        };
        //CSJ,variable=logger,ロガー
        var logger = new FileLogger(Path.Combine(root, "child.log"));
        //CSJ,variable=interrupted,中断済み
        var interrupted = new RelinkSaveService(logger, null, null, () => Environment.Exit(87));
        await AssetRelinkRecovery.StagePreparedOutputAsync(evidence, context, interrupted, default);
        throw new InvalidOperationException("Relink child did not reach the configured interruption point.");
    }

    //CSJ,method=TestRevisionSeriesAsync,版系列をテストする（非同期）
    private static async Task TestRevisionSeriesAsync(string root, string project, AssetMetadata item)
    {
        //CSJ,variable=directory,ディレクトリ
        var directory = Path.Combine(root, "managed-series"); Directory.CreateDirectory(directory);
        //CSJ,variable=initial,初期
        var initial = AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, projectId: Guid.NewGuid());
        var head = await AssetRevisionSeries.InitializeAsync(directory, initial);
        if (head != await AssetRevisionSeries.ReadHeadAsync(directory)) throw new Exception("series head did not persist");
        using (var held = new FileStream(Path.Combine(directory, ".asset-revisions.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { await AssetRevisionSeries.ReadHeadAsync(directory); throw new Exception("held series lock was ignored"); } catch (IOException) { }
        }
        var first = AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, initial, true, projectId: initial.ProjectId);
        var second = AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, initial, true, projectId: initial.ProjectId);
        //CSJ,variable=firstPath,最初パス
        var firstPath = Path.Combine(directory, $"revision-{initial.RevisionId:N}.json");
        //CSJ,variable=oldBytes,旧バイト列
        var oldBytes = await File.ReadAllBytesAsync(firstPath);
        try { await AssetRevisionSeries.InitializeAsync(directory, initial); throw new Exception("series reinitialization accepted"); } catch (InvalidDataException) { }
        var afterReinitialize = await File.ReadAllBytesAsync(firstPath);
        if (!oldBytes.SequenceEqual(afterReinitialize)) throw new Exception("reinitialization altered existing revision");
        //CSJ,method=Append,追記する
        async Task<bool> Append(AssetManifest revision)
        {
            try { await AssetRevisionSeries.AppendAsync(directory, head, revision); return true; }
            catch (IOException) { return false; }
            catch (InvalidDataException) { return false; }
        }
        //CSJ,variable=results,結果一覧
        var results = await Task.WhenAll(Append(first), Append(second));
        if (results.Count(x => x) != 1) throw new Exception("concurrent head updates did not produce exactly one winner");
        var firstAfterUpdate = await File.ReadAllBytesAsync(firstPath);
        if (!oldBytes.SequenceEqual(firstAfterUpdate)) throw new Exception("series update changed historical revision");
        var current = await AssetRevisionSeries.ReadHeadAsync(directory);
        if (current.ProjectId != initial.ProjectId || (current.RevisionId != first.RevisionId && current.RevisionId != second.RevisionId)) throw new Exception("incorrect current head");
        //CSJ,variable=history,履歴
        var history = await AssetRevisionSeries.InspectAsync(directory);
        if (history.Root != head || history.Head != current ||
            !history.Revisions.Select(r => r.Receipt).SequenceEqual(new[] { head, current }))
            throw new Exception("series inspection lost causal order or receipt identity");
        //CSJ,variable=selection,選択
        var selection = await AssetRevisionSeries.ResolveAsync(directory, head.ProjectId, head.RevisionId, head.Sha256, head.RevisionId, head.Sha256);
        if (selection.Selected.Manifest.RevisionId != initial.RevisionId || selection.Head != current)
            throw new Exception("historical selection was lost after a newer revision");
        //CSJ,variable=importPath,取り込みパス
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
        //CSJ,variable=cancelledImport,キャンセル済み取り込み
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
        //CSJ,variable=idCollision,ID衝突
        var idCollision = initial with { ParentRevisionId = current.RevisionId };
        try { await AssetRevisionSeries.AppendAsync(directory, current, idCollision); throw new Exception("historical revision collision accepted"); } catch (IOException) { }
        var incorrectProject = first with { RevisionId = Guid.NewGuid(), ParentRevisionId = current.RevisionId, ProjectId = Guid.NewGuid() };
        try { await AssetRevisionSeries.AppendAsync(directory, current, incorrectProject); throw new Exception("foreign project revision accepted"); } catch (InvalidDataException) { }
        //CSJ,variable=cancelled,キャンセル済み
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await AssetRevisionSeries.ReadHeadAsync(directory, cancelled.Token); throw new Exception("series cancellation ignored"); } catch (OperationCanceledException) { }
        }
        var foreign = initial with { RevisionId = Guid.NewGuid(), ParentRevisionId = initial.RevisionId };
        await AssetManifestStore.SaveNewAsync(Path.Combine(directory, $"revision-{foreign.RevisionId:N}.json"), foreign);
        try { await AssetRevisionSeries.ReadHeadAsync(directory); throw new Exception("divergent head accepted"); } catch (InvalidDataException) { }
        //CSJ,variable=graph,グラフ
        var graph = await AssetRevisionSeries.InspectGraphAsync(directory);
        var secondTip = graph.Heads.Single(tip => tip.RevisionId == foreign.RevisionId);
        if (graph.Root != head || graph.Revisions[0].Receipt != head || graph.Revisions.Count != 3 ||
            graph.Heads.Count != 2 || !graph.Heads.Contains(current))
            throw new Exception("divergent graph did not preserve both tips and their common root");
        var divergentImport = Path.Combine(root, "imported-divergent-history");
        //CSJ,variable=legacyPaths,旧形式パス一覧
        var legacyPaths = graph.Revisions.Reverse().Select(revision =>
            Path.Combine(directory, $"revision-{revision.Receipt.RevisionId:N}.json")).ToArray();
        //CSJ,variable=legacyBytes,旧形式バイト列
        var legacyBytes = new Dictionary<string, byte[]>();
        //CSJ,variable=file,ファイル
        foreach (var file in legacyPaths) legacyBytes.Add(file, await File.ReadAllBytesAsync(file));
        try
        {
            await AssetRevisionSeries.ImportLegacyAsync(divergentImport, head.ProjectId, legacyPaths);
            throw new Exception("linear import exposed a divergent history");
        }
        catch (InvalidDataException) { }
        if (Directory.Exists(divergentImport)) throw new Exception("a rejected import created the destination");
        var importedGraph = await AssetRevisionSeries.ImportLegacyGraphAsync(divergentImport, head.ProjectId, legacyPaths);
        var reopenedGraph = await AssetRevisionSeries.InspectGraphAsync(divergentImport);
        if (importedGraph.Root != head || !graph.Heads.SequenceEqual(importedGraph.Heads) ||
            !graph.Revisions.Select(revision => revision.Receipt).SequenceEqual(reopenedGraph.Revisions.Select(revision => revision.Receipt)))
            throw new Exception("divergent import omitted a tip or changed the history");
        foreach (var (file, originalBytes) in legacyBytes)
        {
            var original = await File.ReadAllBytesAsync(file);
            var copied = await File.ReadAllBytesAsync(Path.Combine(divergentImport, Path.GetFileName(file)));
            if (!originalBytes.SequenceEqual(original) || !originalBytes.SequenceEqual(copied))
                throw new Exception("a divergent legacy import changed revision bytes");
        }
        //CSJ,variable=branch,ブランチ
        var branch = await AssetRevisionSeries.ResolveBranchAsync(directory, head.ProjectId, head.RevisionId, head.Sha256,
            secondTip.RevisionId, secondTip.Sha256);
        if (branch.Selected.Receipt != secondTip || branch.Graph.Heads.Count != 2)
            throw new Exception("explicit branch selection was not resolved");
        //CSJ,variable=branchUpdate,ブランチ更新
        var branchUpdate = AssetManifestStore.Create(project, [item], DateTimeOffset.UtcNow, foreign, true, projectId: head.ProjectId);
        var newTip = await AssetRevisionSeries.AppendToBranchAsync(directory, head, graph.Heads, secondTip, branchUpdate);
        //CSJ,variable=updatedGraph,更新済みグラフ
        var updatedGraph = await AssetRevisionSeries.InspectGraphAsync(directory);
        if (!updatedGraph.Heads.ToHashSet().SetEquals([current, newTip]) || updatedGraph.Revisions.Count != 4)
            throw new Exception("extending one branch dropped or merged another branch");
        //CSJ,variable=concurrentUpdate,並行更新
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

        //CSJ,variable=duplicateDirectory,重複ディレクトリ
        var duplicateDirectory = Path.Combine(root, "duplicate-json-series"); Directory.CreateDirectory(duplicateDirectory);
        //CSJ,variable=duplicatePath,重複パス
        var duplicatePath = Path.Combine(duplicateDirectory, $"revision-{initial.RevisionId:N}.json");
        //CSJ,variable=duplicateJson,重複JSON
        var duplicateJson = JsonSerializer.Serialize(initial).Insert(1, "\"SchemaVersion\":1,");
        await File.WriteAllTextAsync(duplicatePath, duplicateJson);
        try { await AssetRevisionSeries.ReadHeadAsync(duplicateDirectory); throw new Exception("duplicate JSON property accepted"); } catch (InvalidDataException) { }
        if (await File.ReadAllTextAsync(duplicatePath) != duplicateJson) throw new Exception("corrupt revision was altered");
    }
}
