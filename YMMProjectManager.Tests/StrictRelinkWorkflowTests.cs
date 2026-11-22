using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Relink;

//CSJ,class=StrictRelinkWorkflowTests,厳格再リンク作業手順テスト
internal static class StrictRelinkWorkflowTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    internal static async Task RunAsync(string workRoot)
    {
        await Program.RunNamedCaseAsync("R21 prepared relink of a received PF revalidates the pool record on retry", async () =>
        {
            //CSJ,variable=root,ルート
            var root = Directory.CreateDirectory(Path.Combine(workRoot, "strict-pending")).FullName;
            //CSJ,variable=series,系列
            var series = Directory.CreateDirectory(Path.Combine(root, "history")).FullName;
            //CSJ,variable=pool,プール
            var pool = Directory.CreateDirectory(Path.Combine(root, "pool")).FullName;
            var incoming = Directory.CreateDirectory(Path.Combine(root, "approved")).FullName;
            var source = Path.Combine(root, "project.ymmp");
            //CSJ,variable=originalReference,元の参照
            const string originalReference = @"Z:\sender\project\media\clip.wav";
            var json = JsonSerializer.Serialize(new
            {
                FilePath = @"Z:\sender\project\project.ymmp",
                Timelines = new[] { new { Items = new[] { new { FilePath = originalReference, Unknown = "preserved" } } } },
            });
            await File.WriteAllTextAsync(source, json);
            var candidate = Path.Combine(incoming, "clip.wav");
            await File.WriteAllTextAsync(candidate, "synthetic sound bytes");
            var registered = await AssetPool.CollectAsync(pool, candidate, originalReference, new string('a', 40));
            //CSJ,variable=manifest,目録
            var manifest = AssetManifestStore.Create(source,
                [new AssetMetadata(@"media\clip.wav", registered.Record.Size, registered.Record.Sha256, null)], DateTimeOffset.UtcNow);
            var head = await AssetRevisionSeries.InitializeAsync(series, manifest);
            //CSJ,variable=logger,ロガー
            var logger = new FileLogger(Path.Combine(root, "test.log"));
            var (context, _) = await new RelinkScanService(logger).ScanForStrictRelinkAsync(source, default);
            var row = context!.Rows.Single();
            //CSJ,variable=approval,承認
            var approval = (await AssetPool.FindAsync("clip.wav", registered.Record.Sha256, [pool], registered.ManifestPath)).Single();
            row.Status = RelinkStatus.Updated;
            row.SelectedCandidate = approval.CandidatePath;
            //CSJ,variable=review,レビュー
            var review = await AssetRelinkRecovery.ReviewAsync(context, series, head, head, [head], default, requireStrict: true);
            Check(!review.Single().RequiresConfirmation, "Received absolute reference was not matched to its relative metadata.");
            var output = Path.Combine(root, "out.ymmp");
            //CSJ,variable=evidence,証跡
            var evidence = await AssetRelinkRecovery.PrepareStrictManagedOutputAsync(context, output, series, head, head, [head], review,
                [new AssetRelinkRecovery.StrictAssetProof(row.RowIndex, "clip.wav", approval.ManifestPath, approval.ManifestSha256)], default);
            //CSJ,variable=stage,作業段階
            var stage = await AssetRelinkRecovery.StagePreparedOutputAsync(evidence, context, new RelinkSaveService(logger), default);
            Check(stage.Success, stage.ErrorMessage ?? "Strict output stage failed.");
            //CSJ,variable=manifestBytes,目録バイト列
            var manifestBytes = await File.ReadAllBytesAsync(registered.ManifestPath);
            //CSJ,variable=stageBytes,作業段階バイト列
            var stageBytes = await File.ReadAllBytesAsync(evidence + ".output.tmp");
            await File.AppendAllTextAsync(registered.ManifestPath, "\n");
            await ExpectRefusedAsync(() => RecoverAsync());
            //CSJ,variable=stageAfter,作業段階変更後
            var stageAfter = await File.ReadAllBytesAsync(evidence + ".output.tmp");
            Check(!File.Exists(output) && await AssetRevisionSeries.ReadHeadAsync(series) == head &&
                stageBytes.SequenceEqual(stageAfter), "A failed retry changed output or history.");
            await File.WriteAllBytesAsync(registered.ManifestPath, manifestBytes);
            var first = await RecoverAsync();
            var second = await RecoverAsync();
            Check(first == second && File.Exists(output) && !File.Exists(evidence + ".output.tmp"), "Retry was not idempotent.");
            Check(await File.ReadAllTextAsync(source) == json && (await File.ReadAllTextAsync(output)).Contains("preserved"),
                "The source or unknown data was changed.");

            //CSJ,method=RecoverAsync,復旧する（非同期）
            Task<AssetRelinkRecovery.OutputReceipt> RecoverAsync() => AssetRelinkRecovery.CompleteForTransferAsync(evidence, series, head,
                default, source, resumePreparedOutput: true, requireStrictPrepared: true);
        });
        await Program.RunNamedCaseAsync("R21 does not publish a legacy prepared relink that lacks strict matching evidence", async () =>
        {
            //CSJ,variable=root,ルート
            var root = Directory.CreateDirectory(Path.Combine(workRoot, "strict-legacy")).FullName;
            var source = Path.Combine(root, "old.ymmp");
            const string json = """{"Items":[{"FilePath":"old.wav"}]}""";
            await File.WriteAllTextAsync(source, json);
            var candidate = Path.Combine(root, "renamed.wav");
            await File.WriteAllTextAsync(candidate, "legacy candidate");
            //CSJ,variable=series,系列
            var series = Directory.CreateDirectory(Path.Combine(root, "history")).FullName;
            var head = await AssetRevisionSeries.InitializeAsync(series, AssetManifestStore.Create(source, [], DateTimeOffset.UtcNow));
            var context = new RelinkDocumentContext
            {
                YmmpPath = source, OriginalJsonText = json, OriginalBytes = Encoding.UTF8.GetBytes(json),
                Rows = [new RelinkRow { OriginalPath = "old.wav", SelectedCandidate = candidate, Status = RelinkStatus.Updated }],
            };
            var output = Path.Combine(root, "published.ymmp");
            //CSJ,variable=review,レビュー
            var review = await AssetRelinkRecovery.ReviewAsync(context, series, head, head, [head], default);
            //CSJ,variable=evidence,証跡
            var evidence = await AssetRelinkRecovery.PrepareManagedOutputAsync(context, output, series, head, head, [head], review, true, default);
            //CSJ,variable=logger,ロガー
            var logger = new FileLogger(Path.Combine(root, "test.log"));
            Check((await AssetRelinkRecovery.StagePreparedOutputAsync(evidence, context, new RelinkSaveService(logger), default)).Success,
                "Legacy stage fixture failed.");
            await ExpectRefusedAsync(() => AssetRelinkRecovery.CompleteForTransferAsync(evidence, series, head, default, source,
                resumePreparedOutput: true, requireStrictPrepared: true));
            Check(!File.Exists(output) && File.Exists(evidence) && File.Exists(evidence + ".output.tmp") &&
                await File.ReadAllTextAsync(source) == json && await AssetRevisionSeries.ReadHeadAsync(series) == head,
                "Legacy refusal did not retain the project, evidence or metadata.");
        });
        await Program.RunNamedCaseAsync("R21 standalone relink keeps the staged handle through publication", async () =>
        {
            //CSJ,variable=root,ルート
            var root = Directory.CreateDirectory(Path.Combine(workRoot, "strict-owned")).FullName;
            var source = Path.Combine(root, "src.ymmp");
            var output = Path.Combine(root, "out.ymmp");
            const string json = """{"Items":[{"FilePath":"old.wav"}]}""";
            await File.WriteAllTextAsync(source, json);
            //CSJ,variable=originalHash,元のハッシュ
            var originalHash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
            var context = new RelinkDocumentContext
            {
                YmmpPath = source, OriginalJsonText = json, OriginalBytes = Encoding.UTF8.GetBytes(json),
                Rows = [new RelinkRow { OriginalPath = "old.wav", SelectedCandidate = "new.wav", Status = RelinkStatus.Updated }],
            };
            var refused = false;
            //CSJ,variable=logger,ロガー
            var logger = new FileLogger(Path.Combine(root, "test.log"));
            var saver = new RelinkSaveService(logger, null, null, () =>
            {
                //CSJ,variable=temporary,一時
                var temporary = Directory.GetFiles(root, "*.tmp-*").Single();
                try { File.WriteAllText(temporary, """{"wrong":true}"""); }
                catch (IOException) { refused = true; }
            });
            var result = await saver.SaveAsAsync(context, output, default);
            var current = await File.ReadAllBytesAsync(source);
            Check(refused && result.Success && (await File.ReadAllTextAsync(output)).Contains("new.wav") &&
                originalHash.SequenceEqual(SHA256.HashData(current)), "Unowned staged bytes could be published.");
        });
    }

    private static async Task ExpectRefusedAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid staged relink was allowed.");
    }

    //CSJ,method=Check,確認する
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
