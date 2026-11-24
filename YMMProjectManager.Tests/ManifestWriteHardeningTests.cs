using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Generations;

//CSJ,class=ManifestWriteHardeningTests,目録書き込みの安全性テスト
internal static class ManifestWriteHardeningTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    public static async Task RunAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(workRoot, "manifest-r1b2"); Directory.CreateDirectory(root);
        //CSJ,variable=project,プロジェクト
        var project = Path.Combine(root, "synthetic.ymmp"); await File.WriteAllTextAsync(project, "synthetic");
        //CSJ,variable=storageRoot,保管ルート
        var storageRoot = Path.Combine(root, "storage"); var logger = new FileLogger(Path.Combine(root, "test.log"));
        //CSJ,variable=storage,保管
        var storage = new ProjectGenerationStorage(storageRoot); var service = new ProjectGenerationService(logger, storageRoot);
        //CSJ,method=Check,確認する
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        async Task Expect<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
        var first = await service.CreateGenerationAsync(project,"one",null);
        await service.CreateGenerationAsync(project,"two",null);
        var id = new ProjectGenerationHashService().ComputeProjectId(project); var path = storage.GetManifestPath(id);
        //CSJ,variable=snapshot,スナップショット
        var snapshot = Path.Combine(first.GenerationPath,"project.ymmp"); var snapshotHash = Hash(snapshot);
        //CSJ,variable=baseline,基準
        var baseline = Hash(path); var size = new FileInfo(path).Length; var bytes = await File.ReadAllBytesAsync(path);
        var fixedTemp = path + ".tmp"; await File.WriteAllTextAsync(fixedTemp,"unrelated synthetic temp");
        //CSJ,variable=existingTempHash,既存一時ハッシュ
        var existingTempHash = Hash(fixedTemp);
        void CheckPreserved()
        {
            Check(Hash(path)==baseline && new FileInfo(path).Length==size && File.ReadAllBytes(path).SequenceEqual(bytes),"manifest changed");
            Check(Hash(snapshot)==snapshotHash && Hash(fixedTemp)==existingTempHash,"snapshot or unrelated temp changed");
            Check(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!,"manifest.json.tmp-*").Any(),"owned temp remains");
        }
        // Actual create path: partial temp write / disk-full-like failure.
        var failing = new ProjectGenerationStorage(storageRoot, async (stream, data, token) => { await stream.WriteAsync(data.AsMemory(0,data.Length/2),token); throw new IOException("synthetic disk full"); }, null, null);
        var failingService = new ProjectGenerationService(logger, failing);
        //CSJ,variable=directories,ディレクトリ一覧
        var directories = Directory.GetDirectories(Path.GetDirectoryName(first.GenerationPath)!).Length;
        await Expect<IOException>(() => failingService.CreateGenerationAsync(project,"failed",null));
        CheckPreserved(); Check(Directory.GetDirectories(Path.GetDirectoryName(first.GenerationPath)!).Length==directories,"failed create left snapshot");
        using (var cts = new CancellationTokenSource())
        {
            //CSJ,variable=cancelStorage,キャンセル保管
            var cancelStorage = new ProjectGenerationStorage(storageRoot, async (stream, data, token) => { await stream.WriteAsync(data.AsMemory(0, data.Length/2),token); cts.Cancel(); token.ThrowIfCancellationRequested(); }, null, null);
            await Expect<OperationCanceledException>(() => new ProjectGenerationService(logger,cancelStorage).CreateGenerationAsync(project,"cancel",null,cts.Token));
        }
        CheckPreserved();
        await Program.RunNamedCaseAsync("Generation retirement leaves an occupied destination intact", async () =>
        {
            // Exercise the storage boundary after the service's earlier absence check.
            var destination = Path.Combine(storage.GetDeletedDirectory(id), "concurrent-retirement");
            Directory.CreateDirectory(destination);
            var existing = Path.Combine(destination, "existing.ymmp");
            await File.WriteAllTextAsync(existing, "unrelated retained project");
            await Expect<IOException>(() => storage.MoveDirectoryAsync(first.GenerationPath, destination));
            CheckPreserved();
            Check(await File.ReadAllTextAsync(existing) == "unrelated retained project", "previous retirement was overwritten");
        });
        await Program.RunNamedCaseAsync("Cancellation before generation retirement leaves both directories intact", async () =>
        {
            var destination = Path.Combine(storage.GetDeletedDirectory(id), "cancelled-retirement");
            Directory.CreateDirectory(destination);
            var existing = Path.Combine(destination, "existing.ymmp");
            await File.WriteAllTextAsync(existing, "unrelated retained project");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Expect<OperationCanceledException>(() => storage.MoveDirectoryAsync(first.GenerationPath, destination, cancelled.Token));
            CheckPreserved();
            Check(await File.ReadAllTextAsync(existing) == "unrelated retained project", "cancellation removed a previous retirement");
        });
        // The delete path moves then rolls back when the manifest write fails.
        //CSJ,variable=deleted,削除済み
        var deleted = await failingService.DeleteGenerationAsync(project, first.GenerationId);
        Check(!deleted.Success && Directory.Exists(first.GenerationPath),"delete rollback"); CheckPreserved();
        await Program.RunNamedCaseAsync("Generation rollback reports the retained snapshot when the old path is occupied", async () =>
        {
            //CSJ,variable=isolatedProject,隔離プロジェクト
            var isolatedProject = Path.Combine(root, "rollback-collision.ymmp");
            await File.WriteAllTextAsync(isolatedProject, "retained generation");
            //CSJ,variable=generation,世代
            var generation = await service.CreateGenerationAsync(isolatedProject, "rollback", null);
            //CSJ,variable=projectId,プロジェクトID
            var projectId = new ProjectGenerationHashService().ComputeProjectId(isolatedProject);
            //CSJ,variable=manifest,目録
            var manifest = storage.GetManifestPath(projectId);
            //CSJ,variable=initialHash,初期ハッシュ
            var initialHash = Hash(manifest);
            var rollbackBlocked = new ProjectGenerationStorage(storageRoot, null, null, () =>
            {
                Directory.CreateDirectory(generation.GenerationPath);
                File.WriteAllText(Path.Combine(generation.GenerationPath, "separate.txt"), "separate folder");
                throw new IOException("synthetic manifest failure");
            });

            var result = await new ProjectGenerationService(logger, rollbackBlocked).DeleteGenerationAsync(isolatedProject, generation.GenerationId);
            var moved = Directory.EnumerateDirectories(storage.GetDeletedDirectory(projectId)).Single();
            Check(!result.Success && result.ErrorMessage?.Contains(moved, StringComparison.Ordinal) == true &&
                await File.ReadAllTextAsync(Path.Combine(moved, "project.ymmp")) == "retained generation" &&
                await File.ReadAllTextAsync(Path.Combine(generation.GenerationPath, "separate.txt")) == "separate folder" &&
                initialHash == Hash(manifest), "failed rollback hid or changed a retained version");
        });
        await Program.RunNamedCaseAsync("Generation retirement pins the exact directory until rollback", async () =>
        {
            var isolatedProject = Path.Combine(root, "retirement-pin.ymmp");
            await File.WriteAllTextAsync(isolatedProject, "exact generation");
            var generation = await service.CreateGenerationAsync(isolatedProject, "pin", null);
            var projectId = new ProjectGenerationHashService().ComputeProjectId(isolatedProject);
            var manifest = storage.GetManifestPath(projectId);
            var initialHash = Hash(manifest);
            var renameRefused = false;
            var failingStorage = new ProjectGenerationStorage(storageRoot, null, null, () =>
            {
                var retired = Directory.EnumerateDirectories(storage.GetDeletedDirectory(projectId)).Single();
                try { Directory.Move(retired, retired + "-different"); }
                catch (IOException) { renameRefused = true; }
                throw new IOException("synthetic manifest failure");
            });
            var removal = await new ProjectGenerationService(logger, failingStorage).DeleteGenerationAsync(isolatedProject, generation.GenerationId);
            Check(!removal.Success && renameRefused && initialHash == Hash(manifest) &&
                await File.ReadAllTextAsync(Path.Combine(generation.GenerationPath, "project.ymmp")) == "exact generation",
                "rollback did not keep the original generation pinned");
        });
        await Program.RunNamedCaseAsync("Generation retirement retains data when the manifest outcome cannot be verified", async () =>
        {
            var isolatedProject = Path.Combine(root, "retirement-uncertain.ymmp");
            await File.WriteAllTextAsync(isolatedProject, "recoverable generation");
            var generation = await service.CreateGenerationAsync(isolatedProject, "uncertain", null);
            var projectId = new ProjectGenerationHashService().ComputeProjectId(isolatedProject);
            var manifest = storage.GetManifestPath(projectId);
            var failingStorage = new ProjectGenerationStorage(storageRoot, null, null, () =>
            {
                File.WriteAllText(manifest, "unreadable external manifest");
                throw new IOException("synthetic manifest failure");
            });
            var removal = await new ProjectGenerationService(logger, failingStorage).DeleteGenerationAsync(isolatedProject, generation.GenerationId);
            var retired = Directory.EnumerateDirectories(storage.GetDeletedDirectory(projectId)).Single();
            Check(!removal.Success && removal.ErrorMessage?.Contains(retired, StringComparison.Ordinal) == true &&
                await File.ReadAllTextAsync(Path.Combine(retired, "project.ymmp")) == "recoverable generation" &&
                await File.ReadAllTextAsync(manifest) == "unreadable external manifest",
                "an unreadable manifest lost generation recovery evidence");
        });
        var flushFailure = new ProjectGenerationService(logger, new ProjectGenerationStorage(storageRoot, null, _ => throw new IOException("synthetic flush"), null));
        await Expect<IOException>(() => flushFailure.CreateGenerationAsync(project,"flush",null)); CheckPreserved();
        // Actual OS replacement failure, no real ACL changes.
        using (var held = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            await Expect<IOException>(() => service.CreateGenerationAsync(project,"locked",null));
        CheckPreserved();
        Console.WriteLine("R1-B2 partial write manifest SHA-256 before/after: " + baseline + " / " + Hash(path) + "; snapshot: " + snapshotHash + " / " + Hash(snapshot));
        // Modify valid manifest after initial read and before comparison. The external edit survives.
        var externalEdits = 0; string? lastExternalHash = null;
        //CSJ,variable=externalStorage,外部保管
        var externalStorage = new ProjectGenerationStorage(storageRoot, null, null, () => {
            var json = JsonNode.Parse(File.ReadAllText(path))!; json["ProjectFileName"]=$"external-change-{++externalEdits}.ymmp"; File.WriteAllText(path,json.ToJsonString()); lastExternalHash=Hash(path);
        });
        //CSJ,variable=conflictService,競合サービス
        var conflictService = new ProjectGenerationService(logger, externalStorage);
        await Expect<InvalidDataException>(() => conflictService.CreateGenerationAsync(project,"conflict",null));
        Check(JsonNode.Parse(await File.ReadAllTextAsync(path))!["ProjectFileName"]!.GetValue<string>()=="external-change-1.ymmp" && Hash(snapshot)==snapshotHash,"external change lost");
        //CSJ,variable=conflictDelete,競合削除
        var conflictDelete=await conflictService.DeleteGenerationAsync(project,first.GenerationId);
        Check(!conflictDelete.Success && Directory.Exists(first.GenerationPath) && Hash(snapshot)==snapshotHash && Hash(path)==lastExternalHash && externalEdits==2,"conflict delete rollback");
        Check(Hash(fixedTemp)==existingTempHash && !Directory.EnumerateFiles(Path.GetDirectoryName(path)!,"manifest.json.tmp-*").Any(),"conflict temp cleanup");
        // Separate writes to the same target create different owned temporary files.
        //CSJ,variable=tempNames,一時名前一覧
        var tempNames = new List<string>();
        //CSJ,variable=nameStorage,名前保管
        var nameStorage = new ProjectGenerationStorage(storageRoot, async (stream,data,token) => { tempNames.Add(((FileStream)stream).Name); await stream.WriteAsync(data,token); }, null, null);
        //CSJ,variable=sameManifest,同一目録
        var sameManifest = await nameStorage.ReadManifestAsync(id) ?? throw new Exception("manifest missing");
        await nameStorage.WriteManifestAsync(id,sameManifest); await nameStorage.WriteManifestAsync(id,sameManifest);
        Check(tempNames.Count==2 && tempNames[0]!=tempNames[1] && tempNames.All(x=>Path.GetDirectoryName(x)==Path.GetDirectoryName(path)) && Hash(fixedTemp)==existingTempHash,"temporary names");
        // Two writers deliberately start from the same validated bytes: only one can commit.
        //CSJ,variable=repository,リポジトリ
        var repository = new ProjectGenerationManifestRepository(storage, logger);
        var staleA = await repository.LoadAsync(id, project); var staleB = await repository.LoadAsync(id, project);
        staleA.Manifest!.ProjectFileName = "writer-a.ymmp"; staleB.Manifest!.ProjectFileName = "writer-b.ymmp";
        async Task<bool> Commit(ProjectGenerationManifestLoadResult state)
        {
            try { await repository.SaveValidatedAsync(id,state.Manifest!,state,CancellationToken.None); return true; }
            catch (InvalidDataException) { return false; }
        }
        var staleOutcomes=await Task.WhenAll(Commit(staleA),Commit(staleB));
        Check(staleOutcomes.Count(x=>x)==1 && Hash(snapshot)==snapshotHash,"stale writers not fenced");
        // Concurrent service operations: successes must all be reflected; stale writers may fail.
        //CSJ,variable=initialCount,初期件数
        var initialCount=(await service.GetGenerationsAsync(project)).Count;
        var tasks=Enumerable.Range(0,8).Select(async i => { try { await service.CreateGenerationAsync(project,"parallel"+i,null); return true; } catch(InvalidDataException) { return false; } }).ToArray();
        var outcomes=await Task.WhenAll(tasks); var final=await service.GetGenerationsAsync(project);
        Check(final.Count==initialCount+outcomes.Count(x=>x) && final.Any(x=>x.GenerationId==first.GenerationId) && Hash(snapshot)==snapshotHash,"concurrent update lost");
        // Different manifests must enter their write hooks concurrently; no global write gate.
        var project2=Path.Combine(root,"second.ymmp"); await File.WriteAllTextAsync(project2,"second");
        var arrived=0; var both=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parallelStorage = new ProjectGenerationStorage(storageRoot, async (stream,data,token) => { if(Interlocked.Increment(ref arrived)==2) both.TrySetResult(); await both.Task.WaitAsync(TimeSpan.FromSeconds(10)); await stream.WriteAsync(data,token); }, null, null);
        var independent=new ProjectGenerationService(logger,parallelStorage);
        await Task.WhenAll(independent.CreateGenerationAsync(project,"independent",null), independent.CreateGenerationAsync(project2,"independent",null));
        Check(arrived==2 && (await service.GetGenerationsAsync(project2)).Count==1,"different manifest concurrency");
        //CSJ,variable=restored,復元済み
        var restored=await service.RestoreGenerationAsync(project,first.GenerationId,YMMProjectManager.Domain.GenerationRestoreMode.RestoreAsNewFile);
        Check(restored.Success && Hash(snapshot)==snapshotHash,"restore");
        Console.WriteLine("R1-B2 manifest failure, concurrency and external edit checks passed.");
        await GenerationRestoreSafetyTests.RunAsync(Path.Combine(workRoot, "r28-restore"));
    }
}
