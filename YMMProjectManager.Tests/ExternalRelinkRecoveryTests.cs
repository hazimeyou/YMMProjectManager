using System.IO;
using System.Text.Json;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Relink;

//CSJ,class=ExternalRelinkRecoveryTests,外部再リンク復旧テスト
internal static class ExternalRelinkRecoveryTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    internal static async Task RunAsync(string workRoot)
    {
        await Program.RunNamedCaseAsync("R22 publishes an independent derived project with exact source and asset provenance", async () =>
        {
            //CSJ,variable=fixture,テスト用データ
            var fixture = await Fixture.CreateAsync(workRoot, "success");
            //CSJ,variable=evidence,証跡
            var evidence = await fixture.PrepareAsync();
            //CSJ,variable=review,レビュー
            var review = await fixture.ReviewAsync(evidence);
            Ensure(review is { State: AssetRelinkRecovery.ExternalRecoveryState.NotStaged }, "The initial intent was not reported as unstarted.");
            Ensure((await fixture.StageAsync(evidence)).Success, "Could not stage the external output.");
            Ensure((await fixture.ReviewAsync(evidence)).State == AssetRelinkRecovery.ExternalRecoveryState.ReadyToVerify,
                "Staged output was not reported for verification.");
            var registered = 0;
            //CSJ,variable=receipt,照合記録
            var receipt = await fixture.CompleteAsync(evidence, async (ready, token) =>
            {
                Ensure(ready.Output.Revision.ProjectId == review.DerivedProjectId, "The derived identity changed before registration.");
                var child = Path.Combine(ready.SeriesDirectory, $"revision-{ready.Output.Revision.RevisionId:N}.json");
                //CSJ,variable=childBytes,子バイト列
                var childBytes = await File.ReadAllBytesAsync(child, token);
                Ensure(AssetManifestStore.Parse(childBytes).ProjectId == ready.Output.Revision.ProjectId,
                    "The child revision was not readable during registration.");
                EnsureWriteDenied(child);
                EnsureWriteDenied(Path.Combine(fixture.Series, $"revision-{fixture.Head.RevisionId:N}.json"));
                EnsureWriteDenied(fixture.Match.CandidatePath);
                EnsureWriteDenied(fixture.Source);
                EnsureWriteDenied(fixture.Output);
                registered++;
            });
            Ensure(registered == 1 && receipt.Output.Revision.ProjectId != fixture.Head.ProjectId &&
                receipt.SourceRoot == fixture.Head && receipt.SourceParent == fixture.Head, "Source lineage or registration was lost.");
            var output = await File.ReadAllTextAsync(fixture.Output);
            //CSJ,variable=scan,走査
            var scan = await new RelinkScanService(fixture.Logger).ScanForStrictRelinkAsync(fixture.Output, default);
            Ensure(scan.Context!.Rows.Single().OriginalPath == fixture.Match.CandidatePath && output.Contains("original field"),
                "The output did not contain the approved file or preserve unrelated JSON.");
            //CSJ,variable=history,履歴
            var history = await AssetRevisionSeries.InspectGraphAsync(receipt.SeriesDirectory);
            //CSJ,variable=root,ルート
            var root = history.Revisions.Single().Manifest;
            //CSJ,variable=metadata,メタデータ
            var metadata = root.Assets.Single();
            Ensure(root.DerivedFrom is { } provenance && provenance.SourceProjectId == fixture.Head.ProjectId &&
                provenance.SourceRootRevisionId == fixture.Head.RevisionId && provenance.SourceRevisionId == fixture.Head.RevisionId &&
                provenance.SourceRevisionSha256 == fixture.Head.Sha256 && provenance.SourceProjectSha256 == receipt.Output.SourceSha256 &&
                metadata.ExternalRelink is { OriginalFileName: "clip.wav" } && metadata.ExternalRelink.ManifestSha256 == fixture.Match.ManifestSha256 &&
                metadata.ExternalLocation!.RelativePath == Path.GetRelativePath(fixture.Pool, fixture.Match.CandidatePath),
                "The derived manifest does not bind to the source branch and approved pool entry.");
            Ensure(new StrictRelinkAssets(fixture.Output, output, root).FindExpected(fixture.Match.CandidatePath) == metadata,
                "A repeat relink could not resolve the original filename and SHA of the relocated asset.");
            Ensure(await fixture.CompleteAsync(evidence) == receipt, "Recovery issued a different derived ID or published another output.");
            await fixture.CheckOriginalsAsync();
        });
        await Program.RunNamedCaseAsync("R22 requires exact reapproval, unchanged manifest and unchanged asset before completing", async () =>
        {
            //CSJ,variable=fixture,テスト用データ
            var fixture = await Fixture.CreateAsync(workRoot, "reapproval");
            //CSJ,variable=evidence,証跡
            var evidence = await fixture.PrepareAsync();
            Ensure((await fixture.StageAsync(evidence)).Success, "Could not stage the external output.");
            await RefusedAsync(() => fixture.CompleteAsync(evidence, roots: [Path.Combine(workRoot, "unapproved")]));
            //CSJ,variable=manifest,目録
            var manifest = fixture.Match.ManifestPath!;
            //CSJ,variable=manifestBytes,目録バイト列
            var manifestBytes = await File.ReadAllBytesAsync(manifest);
            await File.AppendAllTextAsync(manifest, "\n");
            await RefusedAsync(() => fixture.CompleteAsync(evidence));
            await File.WriteAllBytesAsync(manifest, manifestBytes);
            //CSJ,variable=asset,素材
            var asset = fixture.Match.CandidatePath;
            //CSJ,variable=assetBytes,素材バイト列
            var assetBytes = await File.ReadAllBytesAsync(asset);
            await File.WriteAllTextAsync(asset, "different asset content");
            await RefusedAsync(() => fixture.CompleteAsync(evidence));
            Ensure(!File.Exists(fixture.Output) && File.Exists(evidence + ".output.tmp"), "A refused retry changed the prepared output.");
            await File.WriteAllBytesAsync(asset, assetBytes);
            Ensure(File.Exists((await fixture.CompleteAsync(evidence)).Output.Output), "An exact authorized retry did not complete.");
            await fixture.CheckOriginalsAsync();
        });
        await Program.RunNamedCaseAsync("R22 rejects a destination whose new reference conflicts with existing asset metadata", async () =>
        {
            //CSJ,variable=fixture,テスト用データ
            var fixture = await Fixture.CreateAsync(workRoot, "metadata-alias", cataloguedCandidate: true);
            await RefusedAsync(() => fixture.PrepareAsync());
            Ensure(!File.Exists(fixture.Output) && !Directory.EnumerateFiles(Path.GetDirectoryName(fixture.Source)!, ".ymmpm-relink-*").Any(),
                "An ambiguous derived metadata mapping published an intent or output.");
            await fixture.CheckOriginalsAsync();
        });
        await Program.RunNamedCaseAsync("R22 detects output and derived-directory collisions without replacing them", async () =>
        {
            //CSJ,variable=fixture,テスト用データ
            var fixture = await Fixture.CreateAsync(workRoot, "collision");
            //CSJ,variable=evidence,証跡
            var evidence = await fixture.PrepareAsync();
            //CSJ,variable=review,レビュー
            var review = await fixture.ReviewAsync(evidence);
            Ensure((await fixture.StageAsync(evidence)).Success, "Could not stage the external output.");
            var staged = await File.ReadAllBytesAsync(evidence + ".output.tmp");
            await File.WriteAllBytesAsync(fixture.Output, staged);
            await RefusedAsync(() => fixture.CompleteAsync(evidence));
            //CSJ,variable=collision,衝突
            var collision = await File.ReadAllBytesAsync(fixture.Output);
            Ensure(staged.SequenceEqual(collision), "Collision output bytes changed.");
            File.Delete(fixture.Output);

            var conflictingDirectory = Path.Combine(Path.GetDirectoryName(fixture.Source)!, $".ymmpm-derived-{review.DerivedProjectId:N}");
            Directory.CreateDirectory(conflictingDirectory);
            var unrelated = Path.Combine(conflictingDirectory, "unrelated.txt");
            await File.WriteAllTextAsync(unrelated, "existing data");
            await RefusedAsync(() => fixture.CompleteAsync(evidence));
            Ensure(await File.ReadAllTextAsync(unrelated) == "existing data" && !File.Exists(fixture.Output) &&
                (await File.ReadAllBytesAsync(evidence + ".output.tmp")).AsSpan().SequenceEqual(staged), "A metadata collision changed data.");
            await fixture.CheckOriginalsAsync();
        });
        await Program.RunNamedCaseAsync("R22 distinguishes a new intent from unproven publication artifacts", async () =>
        {
            //CSJ,variable=fixture,テスト用データ
            var fixture = await Fixture.CreateAsync(workRoot, "unproven");
            //CSJ,variable=evidence,証跡
            var evidence = await fixture.PrepareAsync();
            await RefusedAsync(() => fixture.CompleteAsync(evidence));
            Ensure((await fixture.ReviewAsync(evidence)).State == AssetRelinkRecovery.ExternalRecoveryState.NotStaged &&
                !File.Exists(fixture.Output) && File.Exists(evidence), "An unstarted intent should remain reviewable.");
            var unproven = evidence + ".output.tmp";
            await File.WriteAllTextAsync(unproven, "independent ambiguous bytes");
            Ensure((await fixture.ReviewAsync(evidence)).State == AssetRelinkRecovery.ExternalRecoveryState.Unknown,
                "A stage with no witness was not reported as unknown.");
            await RefusedAsync(() => fixture.CompleteAsync(evidence));
            await RefusedAsync(() => fixture.StageAsync(evidence));
            Ensure(await File.ReadAllTextAsync(unproven) == "independent ambiguous bytes" && !File.Exists(fixture.Output),
                "An uncertain outcome replaced an existing artifact.");
            await fixture.CheckOriginalsAsync();
        });
        await Program.RunNamedCaseAsync("R22 resumes after a known staged interruption or interrupted registration", async () =>
        {
            //CSJ,variable=fixture,テスト用データ
            var fixture = await Fixture.CreateAsync(workRoot, "resume");
            //CSJ,variable=evidence,証跡
            var evidence = await fixture.PrepareAsync();
            //CSJ,variable=cancel,キャンセル
            using var cancel = new CancellationTokenSource();
            var saver = new RelinkSaveService(fixture.Logger, null, null, cancel.Cancel);
            try
            {
                await fixture.StageAsync(evidence, saver, cancel.Token);
                throw new InvalidOperationException("The late stage cancellation was not propagated.");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            Ensure(!File.Exists(fixture.Output) && File.Exists(evidence + ".publish"), "The stage interruption lost its witness.");
            //CSJ,variable=interrupted,中断済み
            const string interrupted = "synthetic registration failure";
            try
            {
                await fixture.CompleteAsync(evidence, (_, _) => throw new IOException(interrupted));
                throw new InvalidOperationException("The registration failure was not propagated.");
            }
            catch (IOException ex) when (ex.Message == interrupted) { }
            //CSJ,variable=outputHash,出力ハッシュ
            var outputHash = (await ProjectFileEvidence.ReadAsync(fixture.Output)).Sha256;
            Ensure((await fixture.ReviewAsync(evidence)).State == AssetRelinkRecovery.ExternalRecoveryState.ReadyToVerify,
                "The safely published but unregistered output is not recoverable.");
            var recovered = await fixture.CompleteAsync(evidence);
            Ensure(recovered.Output.OutputSha256 == outputHash && !File.Exists(evidence + ".output.tmp"), "Recovered output evidence differs.");
            await fixture.CheckOriginalsAsync();
        });
        await Program.RunNamedCaseAsync("R22 preserves a stage when the source metadata branch changes", async () =>
        {
            //CSJ,variable=fixture,テスト用データ
            var fixture = await Fixture.CreateAsync(workRoot, "new-tip");
            //CSJ,variable=evidence,証跡
            var evidence = await fixture.PrepareAsync();
            Ensure((await fixture.StageAsync(evidence)).Success, "Could not stage the external output.");
            var current = (await AssetRevisionSeries.InspectAsync(fixture.Series)).Revisions.Single().Manifest;
            var moved = AssetManifestStore.Create(fixture.Source, [], DateTimeOffset.UtcNow, current, confirmedProjectAssociation: true,
                projectId: fixture.Head.ProjectId);
            var newTip = await AssetRevisionSeries.AppendAsync(fixture.Series, fixture.Head, moved);
            await RefusedAsync(() => fixture.CompleteAsync(evidence));
            Ensure(!File.Exists(fixture.Output) && File.Exists(evidence + ".output.tmp") &&
                await AssetRevisionSeries.ReadHeadAsync(fixture.Series) == newTip, "An unrelated history change was lost.");
            Ensure(await File.ReadAllTextAsync(fixture.Source) == fixture.SourceJson, "The original project changed.");
        });
        await Program.RunNamedCaseAsync("R22 will not register a byte-identical replacement output or changed root witness", async () =>
        {
            //CSJ,variable=fixture,テスト用データ
            var fixture = await Fixture.CreateAsync(workRoot, "replaced");
            //CSJ,variable=evidence,証跡
            var evidence = await fixture.PrepareAsync();
            Ensure((await fixture.StageAsync(evidence)).Success, "Could not stage the external output.");
            //CSJ,variable=journal,作業記録
            var journal = await File.ReadAllTextAsync(evidence);
            //CSJ,variable=modified,変更済み
            var modified = System.Text.Json.Nodes.JsonNode.Parse(journal)!.AsObject();
            modified["DerivedRootSha256"] = new string('0', 64);
            await File.WriteAllTextAsync(evidence, modified.ToJsonString());
            await RefusedAsync(() => fixture.CompleteAsync(evidence));
            Ensure(!File.Exists(fixture.Output) && File.Exists(evidence + ".output.tmp"), "Wrong root witness allowed publication.");
            await File.WriteAllTextAsync(evidence, journal);
            //CSJ,variable=published,確定済み
            var published = await fixture.CompleteAsync(evidence);
            var retained = fixture.Output + ".retained";
            File.Move(fixture.Output, retained);
            File.Copy(retained, fixture.Output);
            //CSJ,variable=replacement,置換
            var replacement = await ProjectFileEvidence.ReadAsync(fixture.Output);
            Ensure(replacement.Sha256 == published.Output.OutputSha256 &&
                replacement.FileId != published.Output.OutputIdentity.FileId, "The fixture did not replace the output's physical identity.");
            var callbackReached = false;
            await RefusedAsync(() => fixture.CompleteAsync(evidence, (_, _) =>
            {
                callbackReached = true;
                return Task.CompletedTask;
            }));
            Ensure(!callbackReached && await ProjectFileEvidence.ReadAsync(fixture.Output) == replacement && File.Exists(retained),
                "A replaced output was registered or modified.");
            await fixture.CheckOriginalsAsync();
        });
    }

    //CSJ,record=Fixture,テスト用データ
    private sealed record Fixture(string Source, string Output, string SourceJson, string Pool, string Series,
        AssetRevisionSeries.Head Head, RelinkDocumentContext Context, AssetPoolMatch Match, FileLogger Logger)
    {
        //CSJ,method=CreateAsync,作成する（非同期）
        internal static async Task<Fixture> CreateAsync(string workRoot, string name, bool cataloguedCandidate = false)
        {
            //CSJ,variable=root,ルート
            var root = Directory.CreateDirectory(Path.Combine(workRoot, "r22-ext-" + name)).FullName;
            //CSJ,variable=project,プロジェクト
            var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            //CSJ,variable=pool,プール
            var pool = Directory.CreateDirectory(Path.Combine(cataloguedCandidate ? project : root, "pool")).FullName;
            //CSJ,variable=series,系列
            var series = Directory.CreateDirectory(Path.Combine(root, "metadata")).FullName;
            //CSJ,variable=media,メディア
            var media = Path.Combine(root, "clip.wav");
            await File.WriteAllTextAsync(media, "approved media");
            //CSJ,variable=reference,参照
            const string reference = @"media\clip.wav";
            var collected = await AssetPool.CollectAsync(pool, media, reference, new string('a', 40));
            //CSJ,variable=match,一致
            var match = (await AssetPool.FindAsync("clip.wav", collected.Record.Sha256, [pool], collected.ManifestPath)).Single();
            var source = Path.Combine(project, "source.ymmp");
            var json = JsonSerializer.Serialize(new { Items = new[] { new { FilePath = reference } }, Unknown = "original field" });
            await File.WriteAllTextAsync(source, json);
            //CSJ,variable=assets,素材一覧
            var assets = new List<AssetMetadata> { new(reference, collected.Record.Size, collected.Record.Sha256, null) };
            if (cataloguedCandidate)
                assets.Add(new AssetMetadata(Path.GetRelativePath(project, match.CandidatePath),
                    collected.Record.Size, collected.Record.Sha256, null));
            var start = AssetManifestStore.Create(source, assets, DateTimeOffset.UtcNow);
            var head = await AssetRevisionSeries.InitializeAsync(series, start);
            //CSJ,variable=logger,ロガー
            var logger = new FileLogger(Path.Combine(root, "test.log"));
            var (context, _) = await new RelinkScanService(logger).ScanForStrictRelinkAsync(source, default);
            var row = context!.Rows.Single();
            row.Status = RelinkStatus.Updated;
            row.SelectedCandidate = match.CandidatePath;
            return new Fixture(source, Path.Combine(project, "output.ymmp"), json, pool, series, head, context, match, logger);
        }

        //CSJ,method=PrepareAsync,準備する（非同期）
        internal Task<string> PrepareAsync() => AssetRelinkRecovery.PrepareExternalOutputAsync(Context, Output, Series, Head, Head,
            [Head], [new(Context.Rows.Single().RowIndex, "clip.wav", Match.ManifestPath, Match.ManifestSha256)], [Pool], default);

        internal async Task<AssetRelinkRecovery.ExternalRecoveryReview> ReviewAsync(string evidence) =>
            await AssetRelinkRecovery.InspectExternalRecoveryAsync(evidence, Source, default) ??
            throw new InvalidOperationException("External relink evidence was not recognized.");

        //CSJ,method=StageAsync,準備領域に置く（非同期）
        internal Task<(bool Success, string? ErrorMessage)> StageAsync(string evidence, RelinkSaveService? saver = null,
            CancellationToken token = default) =>
            AssetRelinkRecovery.StageExternalOutputAsync(evidence, Context, saver ?? new RelinkSaveService(Logger), [Pool], token);

        //CSJ,method=CompleteAsync,完了する（非同期）
        internal Task<AssetRelinkRecovery.DerivedOutputReceipt> CompleteAsync(string evidence,
            Func<AssetRelinkRecovery.DerivedOutputReceipt, CancellationToken, Task>? callback = null, string[]? roots = null) =>
            AssetRelinkRecovery.CompleteExternalOutputAsync(evidence, Series, Head, Source, roots ?? [Pool], default, callback);

        internal async Task CheckOriginalsAsync()
        {
            //CSJ,variable=graph,グラフ
            var graph = await AssetRevisionSeries.InspectGraphAsync(Series);
            Ensure(graph.Revisions.Count == 1 && graph.Root == Head && graph.Heads.Single() == Head &&
                await File.ReadAllTextAsync(Source) == SourceJson &&
                await File.ReadAllTextAsync(Match.CandidatePath) == "approved media", "Original project, asset or series changed.");
        }
    }

    private static async Task RefusedAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { return; }
        throw new InvalidOperationException("The unsafe external relink operation was allowed.");
    }

    private static void EnsureWriteDenied(string path)
    {
        try { using var probe = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
        catch (IOException) { return; }
        throw new InvalidOperationException("An exact verified file was not pinned during association.");
    }

    //CSJ,method=Ensure,確認する
    private static void Ensure(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
