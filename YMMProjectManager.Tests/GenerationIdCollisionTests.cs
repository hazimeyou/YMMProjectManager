using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Generations;

//CSJ,class=GenerationIdCollisionTests,世代ID衝突テスト
internal static class GenerationIdCollisionTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    public static async Task RunAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(workRoot, "generation-id-collision"); Directory.CreateDirectory(root);
        //CSJ,variable=project,プロジェクト
        var project = Path.Combine(root, "project.ymmp"); await File.WriteAllTextAsync(project, "original fixture");
        //CSJ,variable=logger,ロガー
        var logger = new FileLogger(Path.Combine(root, "test.log"));
        //CSJ,variable=storage,保管
        var storage = new ProjectGenerationStorage(Path.Combine(root, "storage"));
        var first = await new ProjectGenerationService(logger, storage).CreateGenerationAsync(project, "old", null);
        //CSJ,variable=snapshot,スナップショット
        var snapshot = Path.Combine(first.GenerationPath, "project.ymmp");
        var original = SHA256.HashData(await File.ReadAllBytesAsync(snapshot));
        await File.WriteAllTextAsync(project, "changed fixture");
        //CSJ,variable=collision,衝突
        var collision = new ProjectGenerationService(logger, storage, _ => first.GenerationId);
        try { await collision.CreateGenerationAsync(project, "new", null); throw new Exception("collision was accepted"); }
        catch (IOException) { }
        var after = SHA256.HashData(await File.ReadAllBytesAsync(snapshot));
        if (!original.SequenceEqual(after)) throw new Exception("existing snapshot changed after collision");
        //CSJ,variable=records,記録一覧
        var records = await new ProjectGenerationService(logger, storage).GetGenerationsAsync(project);
        if (records.Count != 1 || records[0].GenerationId != first.GenerationId) throw new Exception("existing manifest entry changed");
        var orphanId = "orphan-fixture";
        //CSJ,variable=projectId,プロジェクトID
        var projectId = new ProjectGenerationHashService().ComputeProjectId(Path.GetFullPath(project));
        var orphan = storage.GetGenerationDirectory(projectId, orphanId);
        Directory.CreateDirectory(orphan);
        await File.WriteAllTextAsync(Path.Combine(orphan, "sentinel"), "preserve");
        try { await new ProjectGenerationService(logger, storage, _ => orphanId).CreateGenerationAsync(project, "new", null); throw new Exception("orphan collision was accepted"); }
        catch (IOException) { }
        if (await File.ReadAllTextAsync(Path.Combine(orphan, "sentinel")) != "preserve") throw new Exception("orphan was changed");
        var victim = Path.Combine(root, "victim"); Directory.CreateDirectory(victim);
        await File.WriteAllTextAsync(Path.Combine(victim, "sentinel"), "preserve");
        //CSJ,variable=invalid,無効
        var invalid = await new ProjectGenerationService(logger, storage).DeleteGenerationAsync(project, @"..\..\..\..\victim");
        if (invalid.Success || !File.Exists(Path.Combine(victim, "sentinel"))) throw new Exception("invalid generation path was accepted");
        //CSJ,variable=invalidRestore,無効復元
        var invalidRestore = await new ProjectGenerationService(logger, storage).RestoreGenerationAsync(project, @"..\..\..\..\victim", GenerationRestoreMode.RestoreAsNewFile);
        if (invalidRestore.Success) throw new Exception("invalid restore path was accepted");
        var unlisted = await new ProjectGenerationService(logger, storage).DeleteGenerationAsync(project, orphanId);
        if (unlisted.Success || !File.Exists(Path.Combine(orphan, "sentinel"))) throw new Exception("unlisted generation was moved");
        //CSJ,variable=restoredPath,復元済みのパス
        var restoredPath = Path.Combine(root, $"project_{first.GenerationId}.ymmp");
        await File.WriteAllTextAsync(restoredPath, "existing synthetic file");
        //CSJ,variable=restoreHash,復元のハッシュ
        var restoreHash = SHA256.HashData(await File.ReadAllBytesAsync(restoredPath));
        //CSJ,variable=restore,復元
        var restore = await new ProjectGenerationService(logger, storage).RestoreGenerationAsync(project, first.GenerationId, GenerationRestoreMode.RestoreAsNewFile);
        var restoreHashAfter = SHA256.HashData(await File.ReadAllBytesAsync(restoredPath));
        if (restore.Success || !restoreHash.SequenceEqual(restoreHashAfter)) throw new Exception("restore as new overwrote existing file");
        var goodSnapshot = await File.ReadAllBytesAsync(snapshot);
        var incumbent = await ProjectFileEvidence.ReadAsync(project);
        var existingBackups = Directory.GetFiles(storage.GetRestoreBackupsDirectory(projectId));
        var intactManifest = await File.ReadAllBytesAsync(storage.GetManifestPath(projectId));
        try
        {
            await File.WriteAllTextAsync(snapshot, "damaged synthetic snapshot");
            var corruptRestore = await new ProjectGenerationService(logger, storage).RestoreGenerationAsync(
                project, first.GenerationId, GenerationRestoreMode.RestoreToOriginalWithBackup);
            if (corruptRestore.Success || corruptRestore.ErrorMessage?.Contains("SHA256", StringComparison.Ordinal) != true)
                throw new Exception("Restore did not report the snapshot checksum mismatch.");
            if (await ProjectFileEvidence.ReadAsync(project) != incumbent ||
                !Directory.GetFiles(storage.GetRestoreBackupsDirectory(projectId)).SequenceEqual(existingBackups) ||
                !(await File.ReadAllBytesAsync(storage.GetManifestPath(projectId))).SequenceEqual(intactManifest))
                throw new Exception("Failed validation changed the incumbent PF, backups or manifest.");
            if (Directory.EnumerateFiles(root, "*.restore.tmp").Any())
                throw new Exception("Restore validation left a temporary file.");
        }
        finally
        {
            await File.WriteAllBytesAsync(snapshot, goodSnapshot);
        }
        //CSJ,variable=manifestPath,目録パス
        var manifestPath = storage.GetManifestPath(projectId);
        //CSJ,variable=manifest,目録
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        manifest["Generations"]![0]!["GenerationId"] = @"..\..\victim";
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        var corruptHash = SHA256.HashData(await File.ReadAllBytesAsync(manifestPath));
        try { await storage.ReadManifestAsync(projectId); throw new Exception("invalid manifest ID was accepted"); }
        catch (InvalidDataException) { }
        try { await new ProjectGenerationService(logger, storage).CreateGenerationAsync(project, "new", null); throw new Exception("invalid manifest was overwritten"); }
        catch (InvalidDataException) { }
        var corruptHashAfter = SHA256.HashData(await File.ReadAllBytesAsync(manifestPath));
        if (!corruptHash.SequenceEqual(corruptHashAfter) || !File.Exists(snapshot)) throw new Exception("invalid manifest or snapshot changed");
        Console.WriteLine("Generation ID collision preserves existing data.");
    }
}
