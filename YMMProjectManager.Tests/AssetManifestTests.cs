using System.IO;
using System.Text.Json;
using YMMProjectManager.Infrastructure;

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
        Console.WriteLine("Project asset immutable revision checks passed.");
    }
}
