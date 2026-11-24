using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Relink;

//CSJ,class=RelinkIntegrityTests,再リンク整合性テスト
internal static class RelinkIntegrityTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    public static async Task RunAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(workRoot, "relink-integrity"); Directory.CreateDirectory(root);
        //CSJ,variable=logger,ロガー
        var logger = new FileLogger(Path.Combine(root,"test.log"));
        //CSJ,method=Check,確認する
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        await Program.RunNamedCaseAsync("Active timeline replacement is unavailable without host acceptance", () =>
        {
            //CSJ,variable=blocked,停止
            var blocked = new TimelineItemRelinkService(logger).ReplaceSelectedItemFilePath(null);
            Check(blocked.Kind == TimelineItemRelinkResultKind.Warning && blocked.Message.Contains("別名保存"),
                "unverified timeline replacement did not suggest file-based SaveAs");
            return Task.CompletedTask;
        });
        await Program.RunNamedCaseAsync("Timeline review rejects a new owner, replaced item, changed path or duplicate binding", () =>
        {
            var owner = new object();
            var item = new object();
            var row = new RelinkRow { RowIndex = 0, Status = RelinkStatus.Updated, OriginalPath = "old.wav", SelectedCandidate = "new.wav" };
            bool CheckReview(object current, object[] items, (int, object)[] targets, string path) =>
                TimelineMediaRelinkService.MatchesReview(owner, current, items, targets, [row], _ => path);

            Check(CheckReview(owner, [item], [(0, item)], "old.wav"), "current review was rejected");
            Check(!CheckReview(new object(), [item], [(0, item)], "old.wav"), "unrelated owner was accepted");
            Check(!CheckReview(owner, [new object()], [(0, item)], "old.wav"), "replaced item was accepted");
            Check(!CheckReview(owner, [item], [(0, item)], "edited.wav"), "edited path was accepted");
            Check(!CheckReview(owner, [item], [(0, item), (0, item)], "old.wav"), "duplicate position was accepted");
            return Task.CompletedTask;
        });
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        async Task<(string Path, RelinkDocumentContext Context)> Fixture(string name, string json)
        {
            var dir=Path.Combine(root,name); Directory.CreateDirectory(dir); var path=Path.Combine(dir,"project.ymmp");
            await File.WriteAllTextAsync(path,json,new UTF8Encoding(false));
            var (ctx,result)=await new RelinkScanService(logger).ScanAsync(path,CancellationToken.None);
            Check(ctx is not null && result.ErrorMessage is null,"scan failed " + name);
            return (path,ctx!);
        }
        //CSJ,method=Select,選択する
        void Select(RelinkRow row,string value) { row.Status=RelinkStatus.Updated; row.SelectedCandidate=value; }
        //CSJ,method=SaveNew,新規を保存する
        async Task<(bool Success, string? ErrorMessage, string? OutputPath)> SaveNew(RelinkSaveService service, RelinkDocumentContext document, CancellationToken token)
        {
            //CSJ,variable=outputPath,出力パス
            var outputPath = document.YmmpPath + ".relinked.ymmp";
            var result = await service.SaveAsAsync(document, outputPath, token);
            return (result.Success, result.ErrorMessage, result.Success ? outputPath : null);
        }
        const string source="{\r\n  \"Items\": [ { \"FilePath\": \"old.png\", \"Unknown\": {\"num\":1e+02} }, {\"FilePath\":\"old.png\"}, {\"FilePath\":\"sound.wav\"} ],\r\n  \"Other\":{\"FilePath\":\"old.png\"}, \"Text\":\"\\u65e5\"\r\n}";
        var (path,ctx)=await Fixture("target",source);
        Select(ctx.Rows[0],"日本語/😀/a.png"); Select(ctx.Rows[1],"b\\folder\\\"b.png"); Select(ctx.Rows[2],"音声.wav");
        var before=Hash(path); var blockedInPlace=await new RelinkSaveService(logger).SaveAsync(ctx,CancellationToken.None); Check(!blockedInPlace.Success && Hash(path)==before,"in-place API did not fail closed"); var result=await SaveNew(new RelinkSaveService(logger),ctx,CancellationToken.None);
        Check(result.Success && result.OutputPath is not null && Hash(path)==before,"target save or backup");
        //CSJ,variable=saved,保存済み
        var saved=await File.ReadAllTextAsync(result.OutputPath!);
        var expected=source.Replace("\"FilePath\": \"old.png\"", "\"FilePath\": " + JsonSerializer.Serialize("日本語/😀/a.png"))
            .Replace("\"FilePath\":\"old.png\"}, {\"FilePath\":\"sound.wav\"}", "\"FilePath\":" + JsonSerializer.Serialize("b\\folder\\\"b.png") + "}, {\"FilePath\":" + JsonSerializer.Serialize("音声.wav") + "}");
        Check(saved==expected,"unrelated JSON bytes changed"); using (JsonDocument.Parse(saved)) { }
        var (rescan,rescanResult)=await new RelinkScanService(logger).ScanAsync(result.OutputPath!,CancellationToken.None);
        Check(rescan is not null && rescanResult.ErrorMessage is null && rescan.Rows[0].OriginalPath=="日本語/😀/a.png" && rescan.Rows[1].OriginalPath=="b\\folder\\\"b.png", "rescan");
        // A new revision never replaces the preceding output.
        //CSJ,variable=backupHash,バックアップハッシュ
        var backupHash=Hash(result.OutputPath!); Select(rescan!.Rows[0],"next.png");
        var again=await SaveNew(new RelinkSaveService(logger),rescan,CancellationToken.None);
        Check(again.Success && again.OutputPath!=result.OutputPath && Hash(result.OutputPath!)==backupHash,"prior output overwritten");
        var (external,externalCtx)=await Fixture("external",source); Select(externalCtx.Rows[0],"next.png");
        await File.WriteAllTextAsync(external,source.Replace("1e+02","123")); var externalHash=Hash(external);
        //CSJ,variable=conflict,競合
        var conflict=await SaveNew(new RelinkSaveService(logger),externalCtx,CancellationToken.None);
        Check(!conflict.Success && conflict.ErrorMessage!.Contains("外部") && Hash(external)==externalHash,"external conflict");
        var (late,lateCtx)=await Fixture("late-conflict",source); Select(lateCtx.Rows[0],"next.png");
        var lateSave=new RelinkSaveService(logger,null,null,()=>File.WriteAllText(late,source.Replace("1e+02","124")));
        var lateResult=await SaveNew(lateSave,lateCtx,CancellationToken.None);
        Check(!lateResult.Success && (await File.ReadAllTextAsync(late)).Contains("124"),"late conflict");
        var escapedDir=Path.Combine(root,"escaped"); Directory.CreateDirectory(escapedDir); var escapedFile=Path.Combine(escapedDir,"project.ymmp");
        const string escapedJson="{\"Items\":[{\"FilePath\":\"old\\u002epng\"}],\"Other\":\"\\u65e5\"}";
        await File.WriteAllTextAsync(escapedFile,escapedJson,new UTF8Encoding(true));
        var escapedScan=await new RelinkScanService(logger).ScanAsync(escapedFile,CancellationToken.None);
        Check(escapedScan.Context is not null && escapedScan.Context.Rows[0].OriginalPath=="old.png","escaped scan");
        var longPath="相対/"+new string('x',300)+"/音声.wav"; Select(escapedScan.Context!.Rows[0],longPath);
        Check((await SaveNew(new RelinkSaveService(logger),escapedScan.Context,CancellationToken.None)).Success,"escaped save");
        var escapedBytes=await File.ReadAllBytesAsync(escapedFile + ".relinked.ymmp");
        Check(escapedBytes.AsSpan().StartsWith(new byte[]{0xef,0xbb,0xbf}) && Encoding.UTF8.GetString(escapedBytes,3,escapedBytes.Length-3)==escapedJson.Replace("\"old\\u002epng\"",JsonSerializer.Serialize(longPath)),"BOM or escaped expression changed");
        var (failure,failureCtx)=await Fixture("failures",source); Select(failureCtx.Rows[0],"next.png"); var original=Hash(failure);
        //CSJ,variable=partial,部分
        var partial=new RelinkSaveService(logger,async (stream,bytes,token) => { await stream.WriteAsync(bytes.AsMemory(0,bytes.Length/2),token); throw new IOException("synthetic disk full"); },null,null);
        Check(!(await SaveNew(partial,failureCtx,CancellationToken.None)).Success && Hash(failure)==original && !File.Exists(failure + ".relinked.ymmp"),"partial failure");
        var flushFail=new RelinkSaveService(logger,null,_=>throw new IOException("flush"),null);
        Check(!(await SaveNew(flushFail,failureCtx,CancellationToken.None)).Success && Hash(failure)==original && !File.Exists(failure + ".relinked.ymmp"),"flush failure");
        var occupied = failure + ".relinked.ymmp";
        await File.WriteAllTextAsync(occupied, "locked destination");
        using(var held=new FileStream(occupied,FileMode.Open,FileAccess.Read,FileShare.Read))
            Check(!(await SaveNew(new RelinkSaveService(logger),failureCtx,CancellationToken.None)).Success,"existing destination refused");
        Check(await File.ReadAllTextAsync(occupied) == "locked destination", "locked destination changed");
        File.Delete(occupied);
        using(var heldSource=new FileStream(failure,FileMode.Open,FileAccess.Read,FileShare.None))
            Check(!(await SaveNew(new RelinkSaveService(logger),failureCtx,CancellationToken.None)).Success && !File.Exists(occupied),"locked source must fail before publication");
        Check(Hash(failure)==original && !Directory.EnumerateFiles(Path.GetDirectoryName(failure)!,"*.tmp-*").Any(),"failure cleanup");
        using (var cts=new CancellationTokenSource()) { cts.Cancel(); try { await SaveNew(new RelinkSaveService(logger),failureCtx,cts.Token); throw new Exception("cancel ignored"); } catch(OperationCanceledException) { } }
        Check(Hash(failure)==original,"cancellation");
        var failureAfterHash=Hash(failure);
        //CSJ,variable=afterScan,変更後走査
        var afterScan=await new RelinkScanService(logger).ScanAsync(failure,CancellationToken.None); Select(afterScan.Context!.Rows[0],"other.png");
        //CSJ,variable=concurrent,並行
        var concurrent=await Task.WhenAll(SaveNew(new RelinkSaveService(logger),failureCtx,CancellationToken.None),SaveNew(new RelinkSaveService(logger),afterScan.Context,CancellationToken.None));
        Check(concurrent.Count(x=>x.Success)==1,"concurrent writes not fenced");
        var (bad,badCtx)=await Fixture("bad-index",source); Select(badCtx.Rows[0],"next.png"); badCtx.Rows.Add(new RelinkRow {RowIndex=0,OriginalPath="old.png",Status=RelinkStatus.Updated,SelectedCandidate="wrong.png"});
        var badHash=Hash(bad); Check(!(await SaveNew(new RelinkSaveService(logger),badCtx,CancellationToken.None)).Success && Hash(bad)==badHash,"duplicate target");
        foreach(var (name,json,candidate) in new[] { ("psd","{\"Items\":[{\"FilePath\":\"old.psd\",\"EnableLayersFilePath\":\"old.psd\"}]}","new.psd"), ("sequence","{\"Items\":[{\"$type\":\"VideoItem\",\"FilePath\":\"frame001.png\"}]}","frame001.png") })
        {
            var (file,model)=await Fixture(name,json); Select(model.Rows[0],candidate); var prior=Hash(file);
            Check(!(await SaveNew(new RelinkSaveService(logger),model,CancellationToken.None)).Success && Hash(file)==prior,"special format must be refused");
        }
        //CSJ,variable=videoJson,動画JSON
        var videoJson = "{\"Items\":[{\"$type\":\"VideoItem\",\"FilePath\":\"C:\\\\missing\\\\old.mp4\"}]}";
        var (videoFile,videoContext)=await Fixture("video",videoJson);
        Select(videoContext.Rows[0],@"C:\synthetic\new.mp4");
        //CSJ,variable=videoSaved,動画保存済み
        var videoSaved=await SaveNew(new RelinkSaveService(logger),videoContext,CancellationToken.None);
        Check(videoSaved.Success && (await new RelinkScanService(logger).ScanAsync(videoSaved.OutputPath!,CancellationToken.None)).Context!.Rows[0].OriginalPath==@"C:\synthetic\new.mp4","absolute video path");
        var (nestedFile,nestedContext)=await Fixture("nested-unknown","{\"Items\":[{\"FilePath\":\"old.png\",\"Plugin\":{\"FilePath\":\"old.png\"}}]}");
        Select(nestedContext.Rows[1],"unexpected.png"); var nestedHash=Hash(nestedFile);
        Check(!(await SaveNew(new RelinkSaveService(logger),nestedContext,CancellationToken.None)).Success && Hash(nestedFile)==nestedHash,"nested plugin path must be refused");
        //CSJ,variable=timelineRow,タイムライン行
        var timelineRow=new RelinkRow {RowIndex=0, OriginalPath="old.psd", TypeHint="ImageItem", Status=RelinkStatus.Updated, SelectedCandidate="new.psd"};
        //CSJ,variable=timelineContext,タイムラインコンテキスト
        var timelineContext=new TimelineRelinkContext {Info=null!, Targets=[], Rows=[timelineRow]};
        //CSJ,variable=timelineResult,タイムライン結果
        var timelineResult=new TimelineMediaRelinkService(logger).Save(timelineContext);
        Check(!timelineResult.Success && timelineResult.UpdatedCount==0 && timelineResult.ErrorMessage!.Contains("PSD"),"timeline PSD must be refused before mutation");
        var staleTimeline = new TimelineRelinkContext { Info = null!, Targets = [], Rows = [new RelinkRow { RowIndex = 0, OriginalPath = "old.mp4", TypeHint = "VideoItem", Status = RelinkStatus.Updated, SelectedCandidate = "new.mp4" }] };
        var staleResult = new TimelineMediaRelinkService(logger).Save(staleTimeline);
        Check(!staleResult.Success && staleResult.UpdatedCount == 0 && staleResult.ErrorMessage!.Contains("変更"), "missing timeline target must fail before mutation");
        var row=new RelinkRow {Status=RelinkStatus.Ambiguous, CandidateCount=2}; row.SelectedCandidate="candidate";
        Check(row.Status==RelinkStatus.Updated && row.IsEditable,"manual selection state");
        var (saveAsSource, saveAsContext) = await Fixture("save-as", source);
        Select(saveAsContext.Rows[0], "new.png"); var saveAsHash = Hash(saveAsSource);
        var output = Path.Combine(Path.GetDirectoryName(saveAsSource)!, "copy.ymmp");
        var saveAs = await new RelinkSaveService(logger).SaveAsAsync(saveAsContext, output, CancellationToken.None);
        Check(saveAs.Success && Hash(saveAsSource) == saveAsHash && File.Exists(output), "save-as changed original or failed");
        var otherDir = Path.Combine(root, "different-output"); Directory.CreateDirectory(otherDir);
        Check(!(await new RelinkSaveService(logger).SaveAsAsync(saveAsContext, Path.Combine(otherDir, "copy.ymmp"), CancellationToken.None)).Success && Hash(saveAsSource) == saveAsHash, "cross-directory save changed relative reference base");
        //CSJ,variable=outputHash,出力ハッシュ
        var outputHash = Hash(output);
        Check(!(await new RelinkSaveService(logger).SaveAsAsync(saveAsContext, output, CancellationToken.None)).Success && Hash(output) == outputHash, "save-as overwrote destination");
        var raced = Path.Combine(Path.GetDirectoryName(saveAsSource)!, "race.ymmp");
        var raceSave = new RelinkSaveService(logger, null, null, () => File.WriteAllText(raced, "external"));
        Check(!(await raceSave.SaveAsAsync(saveAsContext, raced, CancellationToken.None)).Success && File.ReadAllText(raced) == "external" && Hash(saveAsSource) == saveAsHash, "save-as destination race");
        var (changedSource, changedContext) = await Fixture("save-as-source-change", source);
        Select(changedContext.Rows[0], "new.png");
        //CSJ,variable=changedOutput,変更済み出力
        var changedOutput = Path.Combine(Path.GetDirectoryName(changedSource)!, "copy.ymmp");
        //CSJ,variable=changedSave,変更済み保存
        var changedSave = new RelinkSaveService(logger, null, null, () => File.WriteAllText(changedSource, "external change"));
        Check(!(await changedSave.SaveAsAsync(changedContext, changedOutput, CancellationToken.None)).Success && File.ReadAllText(changedSource) == "external change" && !File.Exists(changedOutput), "save-as source conflict");
        //CSJ,variable=failedOutput,失敗出力
        var failedOutput = Path.Combine(Path.GetDirectoryName(saveAsSource)!, "write-failure.ymmp");
        //CSJ,variable=failedSave,失敗保存
        var failedSave = new RelinkSaveService(logger, async (stream, bytes, token) => { await stream.WriteAsync(bytes.AsMemory(0, bytes.Length / 2), token); throw new IOException("synthetic partial write"); }, null, null);
        Check(!(await failedSave.SaveAsAsync(saveAsContext, failedOutput, CancellationToken.None)).Success && !File.Exists(failedOutput) && Hash(saveAsSource) == saveAsHash, "save-as partial failure");
        //CSJ,variable=candidateFile,候補ファイル
        var candidateFile = Path.Combine(Path.GetDirectoryName(saveAsSource)!, "candidate.png");
        await File.WriteAllTextAsync(candidateFile, "synthetic candidate");
        var hashes = await RelinkCandidateVerifier.HashSelectedAsync([new RelinkRow { Status = RelinkStatus.Updated, SelectedCandidate = candidateFile }], CancellationToken.None);
        Check(hashes.Count == 1 && hashes[0].Contains(Hash(candidateFile)), "candidate hash missing");
        //CSJ,variable=mutable,変更可能
        var mutable = new RelinkRow { RowIndex = 0, OriginalPath = "old.png", Status = RelinkStatus.Updated, SelectedCandidate = candidateFile };
        var frozen = RelinkCandidateVerifier.SnapshotRows([mutable]);
        mutable.SelectedCandidate = Path.Combine(root, "another.png");
        var frozenHashes = await RelinkCandidateVerifier.HashSelectedAsync(frozen, CancellationToken.None);
        Check(frozen[0].SelectedCandidate == candidateFile && frozenHashes[0].Contains(candidateFile), "candidate snapshot changed during verification");
        var (mutableSource, mutableContext) = await Fixture("mutable-save-as", source);
        Select(mutableContext.Rows[0], "first.png");
        //CSJ,variable=immutableOutput,不変出力
        var immutableOutput = Path.Combine(Path.GetDirectoryName(mutableSource)!, "copy.ymmp");
        //CSJ,variable=snapshotSave,スナップショット保存
        var snapshotSave = new RelinkSaveService(logger, async (stream, bytes, token) => { mutableContext.Rows[0].SelectedCandidate = "second.png"; await stream.WriteAsync(bytes, token); }, null, null);
        Check((await snapshotSave.SaveAsAsync(mutableContext, immutableOutput, CancellationToken.None)).Success && (await File.ReadAllTextAsync(immutableOutput)).Contains("first.png") && !(await File.ReadAllTextAsync(immutableOutput)).Contains("second.png"), "save-as did not freeze target");
        Console.WriteLine("R1-C original SHA-256 before/after injected failure: " + original + " / " + failureAfterHash);
        Console.WriteLine("R1-C isolated relink integrity checks passed.");
    }
}
