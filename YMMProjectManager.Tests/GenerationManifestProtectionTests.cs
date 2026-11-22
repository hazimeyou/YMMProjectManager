using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Generations;

//CSJ,class=GenerationManifestProtectionTests,世代目録の保護テスト
internal static class GenerationManifestProtectionTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    public static async Task RunAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(workRoot, "manifest-protection"); Directory.CreateDirectory(root);
        //CSJ,variable=project,プロジェクト
        var project = Path.Combine(root, "synthetic.ymmp"); await File.WriteAllTextAsync(project, "synthetic project");
        //CSJ,variable=storageRoot,保管ルート
        var storageRoot = Path.Combine(root, "storage"); var logger = new FileLogger(Path.Combine(root, "log.txt"));
        //CSJ,variable=service,サービス
        var service = new ProjectGenerationService(logger, storageRoot);
        //CSJ,method=Check,確認する
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        async Task ExpectFailure(Func<Task> action) { try { await action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Expected invalid manifest failure"); }
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var first = await service.CreateGenerationAsync(project, "first", null);
        var second = await service.CreateGenerationAsync(project, "second", null);
        var projectDir = service.GetProjectDirectory(project); var manifestPath = Path.Combine(projectDir, "manifest.json");
        //CSJ,variable=snapshot,スナップショット
        var snapshot = Path.Combine(first.GenerationPath, "project.ymmp"); var snapshotHash = Hash(snapshot);
        //CSJ,variable=metadata,メタデータ
        var metadata = Path.Combine(first.GenerationPath, "metadata.json"); var metadataHash = Hash(metadata);
        var original = await File.ReadAllTextAsync(manifestPath);
        //CSJ,variable=projectId,プロジェクトID
        var projectId = new ProjectGenerationHashService().ComputeProjectId(project);
        var directRepository = new ProjectGenerationManifestRepository(new ProjectGenerationStorage(storageRoot), logger);
        //CSJ,variable=validManifest,有効目録
        var validManifest = JsonSerializer.Deserialize<ProjectGenerationManifest>(original)!;
        Check((await service.GetGenerationsAsync(project)).Count == 2, "normal list");
        //CSJ,variable=empty,空
        var empty = JsonNode.Parse(original)!; empty["Generations"] = new JsonArray();
        await File.WriteAllTextAsync(manifestPath, empty.ToJsonString());
        Check((await service.GetGenerationsAsync(project)).Count == 0, "valid empty manifest");
        await File.WriteAllTextAsync(manifestPath, original);
        //CSJ,variable=missing,欠落
        var missing = JsonNode.Parse(original)!; missing.AsObject().Remove("Generations");
        var wrong = JsonNode.Parse(original)!; wrong["Generations"] = 42;
        var future = JsonNode.Parse(original)!; future["SchemaVersion"] = 999;
        var badItem = JsonNode.Parse(original)!; badItem["Generations"]![0]!.AsObject().Remove("GenerationId");
        foreach (var (name, content, expected) in new[] {
            ("invalid", "{invalid", ProjectGenerationManifestStatus.Corrupted),
            ("empty", "", ProjectGenerationManifestStatus.Corrupted),
            ("missing-field", missing.ToJsonString(), ProjectGenerationManifestStatus.Corrupted),
            ("wrong-type", wrong.ToJsonString(), ProjectGenerationManifestStatus.Corrupted),
            ("unsupported", future.ToJsonString(), ProjectGenerationManifestStatus.UnsupportedSchema),
            ("missing-item-field", badItem.ToJsonString(), ProjectGenerationManifestStatus.Corrupted) })
        {
            await File.WriteAllTextAsync(manifestPath, content); var before = Hash(manifestPath);
            //CSJ,variable=directories,ディレクトリ一覧
            var directories = Directory.GetDirectories(Path.Combine(projectDir,"generations")).Length;
            Check((await service.GetDiagnosticsAsync(project)).ManifestStatus == expected, name + " status");
            await ExpectFailure(() => service.GetGenerationsAsync(project));
            await ExpectFailure(() => service.CreateGenerationAsync(project, "blocked", null));
            await ExpectFailure(() => directRepository.SaveAsync(projectId, validManifest));
            var deletion = await service.DeleteGenerationAsync(project, first.GenerationId);
            Check(!deletion.Success && !string.IsNullOrEmpty(deletion.ErrorMessage), name + " delete");
            Check(before == Hash(manifestPath) && snapshotHash == Hash(snapshot) && metadataHash == Hash(metadata), name + " data changed");
            Check(Directory.GetDirectories(Path.Combine(projectDir,"generations")).Length == directories, name + " snapshot creation");
            if(name == "invalid") Console.WriteLine("R1-B invalid manifest SHA-256 before/after: " + before + " / " + Hash(manifestPath) + "; snapshot before/after: " + snapshotHash + " / " + Hash(snapshot));
        }
        // Sharing violation must not be mistaken for a missing manifest.
        await File.WriteAllTextAsync(manifestPath, original); var manifestHash = Hash(manifestPath);
        using (var held = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await ExpectFailure(() => service.CreateGenerationAsync(project, "locked", null));
            var deletion = await service.DeleteGenerationAsync(project, first.GenerationId); Check(!deletion.Success, "locked delete");
        }
        Check(Hash(manifestPath)==manifestHash && Hash(snapshot)==snapshotHash, "locked data changed");
        // Missing manifest plus old generations must not create a new manifest or move the snapshot.
        File.Delete(manifestPath);
        await ExpectFailure(() => service.CreateGenerationAsync(project, "missing", null));
        //CSJ,variable=missingDelete,欠落削除
        var missingDelete = await service.DeleteGenerationAsync(project, first.GenerationId);
        Check(!missingDelete.Success && !File.Exists(manifestPath) && Hash(snapshot)==snapshotHash, "missing manifest with snapshots");
        // Direct restore uses metadata and snapshot, independently of the manifest.
        //CSJ,variable=restored,復元済み
        var restored = await service.RestoreGenerationAsync(project, first.GenerationId, GenerationRestoreMode.RestoreAsNewFile);
        Check(restored.Success, "direct restore");
        // Repair is performed only on this synthetic fixture; normal additional generation preserves both old records.
        await File.WriteAllTextAsync(manifestPath, original);
        await service.CreateGenerationAsync(project, "third", null);
        //CSJ,variable=list,一覧
        var list = await service.GetGenerationsAsync(project);
        Check(list.Count == 3 && list.Any(x => x.GenerationId == first.GenerationId) && list.Any(x => x.GenerationId == second.GenerationId), "normal add after repair");
        // Existing storage writes a temp file; cancellation and sharing violation preserve the old manifest.
        var hashBeforeFailure = Hash(manifestPath);
        //CSJ,variable=storage,保管
        var storage = new ProjectGenerationStorage(storageRoot); var loaded = await storage.ReadManifestAsync(projectId) ?? throw new Exception("load");
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel(); try { await storage.WriteManifestAsync(projectId, loaded, cts.Token); throw new Exception("cancel missing"); } catch (OperationCanceledException) { }
        }
        Check(Hash(manifestPath)==hashBeforeFailure && !File.Exists(manifestPath+".tmp"), "cancelled manifest write");
        using (var held = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { await storage.WriteManifestAsync(projectId, loaded); throw new Exception("sharing failure missing"); } catch (IOException) { }
        }
        Check(Hash(manifestPath)==hashBeforeFailure && !File.Exists(manifestPath+".tmp") && Hash(snapshot)==snapshotHash, "replacement failure");
        Console.WriteLine("Generation manifest protection regression checks passed.");
    }
}
