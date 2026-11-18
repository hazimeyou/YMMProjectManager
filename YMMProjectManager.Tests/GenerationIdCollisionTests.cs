using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Generations;

internal static class GenerationIdCollisionTests
{
    public static async Task RunAsync(string workRoot)
    {
        var root = Path.Combine(workRoot, "generation-id-collision"); Directory.CreateDirectory(root);
        var project = Path.Combine(root, "project.ymmp"); await File.WriteAllTextAsync(project, "original fixture");
        var logger = new FileLogger(Path.Combine(root, "test.log"));
        var storage = new ProjectGenerationStorage(Path.Combine(root, "storage"));
        var first = await new ProjectGenerationService(logger, storage).CreateGenerationAsync(project, "old", null);
        var snapshot = Path.Combine(first.GenerationPath, "project.ymmp");
        var original = SHA256.HashData(await File.ReadAllBytesAsync(snapshot));
        await File.WriteAllTextAsync(project, "changed fixture");
        var collision = new ProjectGenerationService(logger, storage, _ => first.GenerationId);
        try { await collision.CreateGenerationAsync(project, "new", null); throw new Exception("collision was accepted"); }
        catch (IOException) { }
        var after = SHA256.HashData(await File.ReadAllBytesAsync(snapshot));
        if (!original.SequenceEqual(after)) throw new Exception("existing snapshot changed after collision");
        var records = await new ProjectGenerationService(logger, storage).GetGenerationsAsync(project);
        if (records.Count != 1 || records[0].GenerationId != first.GenerationId) throw new Exception("existing manifest entry changed");
        var orphanId = "orphan-fixture";
        var projectId = new ProjectGenerationHashService().ComputeProjectId(Path.GetFullPath(project));
        var orphan = storage.GetGenerationDirectory(projectId, orphanId);
        Directory.CreateDirectory(orphan);
        await File.WriteAllTextAsync(Path.Combine(orphan, "sentinel"), "preserve");
        try { await new ProjectGenerationService(logger, storage, _ => orphanId).CreateGenerationAsync(project, "new", null); throw new Exception("orphan collision was accepted"); }
        catch (IOException) { }
        if (await File.ReadAllTextAsync(Path.Combine(orphan, "sentinel")) != "preserve") throw new Exception("orphan was changed");
        var victim = Path.Combine(root, "victim"); Directory.CreateDirectory(victim);
        await File.WriteAllTextAsync(Path.Combine(victim, "sentinel"), "preserve");
        var invalid = await new ProjectGenerationService(logger, storage).DeleteGenerationAsync(project, @"..\..\..\..\victim");
        if (invalid.Success || !File.Exists(Path.Combine(victim, "sentinel"))) throw new Exception("invalid generation path was accepted");
        var invalidRestore = await new ProjectGenerationService(logger, storage).RestoreGenerationAsync(project, @"..\..\..\..\victim", GenerationRestoreMode.RestoreAsNewFile);
        if (invalidRestore.Success) throw new Exception("invalid restore path was accepted");
        var unlisted = await new ProjectGenerationService(logger, storage).DeleteGenerationAsync(project, orphanId);
        if (unlisted.Success || !File.Exists(Path.Combine(orphan, "sentinel"))) throw new Exception("unlisted generation was moved");
        var restoredPath = Path.Combine(root, $"project_{first.GenerationId}.ymmp");
        await File.WriteAllTextAsync(restoredPath, "existing synthetic file");
        var restoreHash = SHA256.HashData(await File.ReadAllBytesAsync(restoredPath));
        var restore = await new ProjectGenerationService(logger, storage).RestoreGenerationAsync(project, first.GenerationId, GenerationRestoreMode.RestoreAsNewFile);
        var restoreHashAfter = SHA256.HashData(await File.ReadAllBytesAsync(restoredPath));
        if (restore.Success || !restoreHash.SequenceEqual(restoreHashAfter)) throw new Exception("restore as new overwrote existing file");
        var createTemp = typeof(ProjectGenerationService).GetMethod("CreateValidatedRestoreTempAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        try
        {
            await (Task<string>)createTemp.Invoke(null, new object[] { snapshot, new string('0', 64), root, CancellationToken.None })!;
            throw new Exception("restore accepted a bad hash");
        }
        catch (InvalidDataException) { }
        if (Directory.EnumerateFiles(root, "*.restore.tmp").Any()) throw new Exception("restore validation left a temporary file");
        var manifestPath = storage.GetManifestPath(projectId);
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
