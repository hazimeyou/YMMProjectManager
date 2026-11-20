using System.IO;
using System.Diagnostics;
using System.Text.Json;
using YMMProjectManager.Infrastructure;

internal static class AssetInventoryTests
{
    public static async Task RunAsync(string workRoot)
    {
        var root = Path.Combine(workRoot, "asset-inventory"); Directory.CreateDirectory(root);
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        await File.WriteAllTextAsync(Path.Combine(root, "asset.txt"), "sample");
        var asset = await AssetInventory.InspectAsync(root, "asset.txt", "https://example.invalid/asset");
        Check(asset.Size == 6 && await AssetInventory.VerifyAsync(root, asset) == AssetVerification.Valid, "valid asset");
        var output = Path.Combine(root, "assets.json"); await AssetInventory.ExportAsync(output, [asset]);
        var imported = await AssetInventory.LoadAsync(output);
        Check(imported.Count == 1 && imported[0] == asset, "metadata roundtrip");
        var before = await File.ReadAllBytesAsync(output);
        try { await AssetInventory.ExportAsync(output, [asset]); throw new Exception("overwrite accepted"); } catch (IOException) { }
        var after = await File.ReadAllBytesAsync(output);
        Check(before.SequenceEqual(after), "existing manifest changed");
        await File.WriteAllTextAsync(Path.Combine(root, "asset.txt"), "change");
        Check(await AssetInventory.VerifyAsync(root, asset) == AssetVerification.HashMismatch, "hash mismatch");
        var project = Path.Combine(root, "inventory.ymmp");
        var outside = Path.Combine(workRoot, "outside-media.wav");
        await File.WriteAllTextAsync(outside, "outside");
        await File.WriteAllTextAsync(Path.Combine(root, "present.wav"), "present");
        await File.WriteAllTextAsync(project, JsonSerializer.Serialize(new
        {
            Items = new[] { "asset.txt", "missing.wav", "present.wav", ".\\PRESENT.wav", outside, "NUL.wav", "present.wav:private" }
                .Select(path => new { FilePath = path })
        }));
        var scan = await AssetInventory.ScanProjectAsync(project, [asset]);
        Check(scan.Count == 6, "equivalent references were not deduplicated");
        Check(scan.Single(item => item.Path == "asset.txt") is { Status: AssetReferenceStatus.Changed, Actual.Size: 6 }
            && scan.Single(item => item.Path == "asset.txt").Expected == asset, "changed reference status");
        Check(scan.Single(item => item.Path == "missing.wav") is { Status: AssetReferenceStatus.Missing, Actual: null }, "missing reference status");
        Check(scan.Single(item => item.Path == "present.wav") is { Status: AssetReferenceStatus.Present, Actual.Size: 7 }, "present reference status");
        Check(scan.Single(item => item.Path == outside) is { Status: AssetReferenceStatus.External, Actual: null }, "external reference was opened");
        Check(scan.Count(item => item.Status == AssetReferenceStatus.Unavailable && item.Actual is null) == 2, "reserved path checks");
        File.Delete(Path.Combine(root, "asset.txt"));
        Check(await AssetInventory.VerifyAsync(root, asset) == AssetVerification.Missing, "missing asset");
        try { await AssetInventory.InspectAsync(root, "../outside"); throw new Exception("traversal accepted"); } catch (ArgumentException) { }
        try { await AssetInventory.InspectAsync(root, "missing", "https://user:password@example.invalid/a"); throw new Exception("credentials accepted"); } catch (ArgumentException) { }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var cancelledOutput = Path.Combine(root, "cancelled.json");
        try { await AssetInventory.ExportAsync(cancelledOutput, [asset], cancelled.Token); throw new Exception("cancel ignored"); } catch (OperationCanceledException) { }
        Check(!File.Exists(cancelledOutput), "cancelled export left output");
        var real = Path.Combine(root, "real"); var realProject = Path.Combine(real, "project");
        var alias = Path.Combine(root, "alias"); Directory.CreateDirectory(realProject);
        await File.WriteAllTextAsync(Path.Combine(realProject, "asset.txt"), "synthetic");
        var junction = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/d", "/c", "mklink", "/J", alias, real }) junction.ArgumentList.Add(arg);
        using (var process = Process.Start(junction)!)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); await stdout; await stderr;
            if (process.ExitCode != 0) Program.ReportConditionalSkip("junction fixture unavailable");
            else
            {
                try { await AssetInventory.InspectAsync(Path.Combine(alias, "project"), "asset.txt"); throw new Exception("ancestor junction accepted"); }
                catch (IOException) { }
            }
        }
        Console.WriteLine("Offline asset metadata checks passed.");
    }
}
