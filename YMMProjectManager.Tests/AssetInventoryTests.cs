using System.IO;
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
        File.Delete(Path.Combine(root, "asset.txt"));
        Check(await AssetInventory.VerifyAsync(root, asset) == AssetVerification.Missing, "missing asset");
        try { await AssetInventory.InspectAsync(root, "../outside"); throw new Exception("traversal accepted"); } catch (ArgumentException) { }
        try { await AssetInventory.InspectAsync(root, "missing", "https://user:password@example.invalid/a"); throw new Exception("credentials accepted"); } catch (ArgumentException) { }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var cancelledOutput = Path.Combine(root, "cancelled.json");
        try { await AssetInventory.ExportAsync(cancelledOutput, [asset], cancelled.Token); throw new Exception("cancel ignored"); } catch (OperationCanceledException) { }
        Check(!File.Exists(cancelledOutput), "cancelled export left output");
        Console.WriteLine("Offline asset metadata checks passed.");
    }
}
