using System.IO;
using System.Diagnostics;
using System.Text.Json;
using YMMProjectManager.Infrastructure;

//CSJ,class=AssetInventoryTests,素材の棚卸しテスト
internal static class AssetInventoryTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    public static async Task RunAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(workRoot, "asset-inventory"); Directory.CreateDirectory(root);
        //CSJ,method=Check,確認する
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        await File.WriteAllTextAsync(Path.Combine(root, "asset.txt"), "sample");
        //CSJ,variable=asset,素材
        var asset = await AssetInventory.InspectAsync(root, "asset.txt", "https://example.invalid/asset");
        Check(asset.Size == 6 && await AssetInventory.VerifyAsync(root, asset) == AssetVerification.Valid, "valid asset");
        var output = Path.Combine(root, "assets.json"); await AssetInventory.ExportAsync(output, [asset]);
        var imported = await AssetInventory.LoadAsync(output);
        Check(imported.Count == 1 && imported[0] == asset, "metadata roundtrip");
        var before = await File.ReadAllBytesAsync(output);
        try { await AssetInventory.ExportAsync(output, [asset]); throw new Exception("overwrite accepted"); } catch (IOException) { }
        var after = await File.ReadAllBytesAsync(output);
        Check(before.SequenceEqual(after), "existing manifest changed");
        await Program.RunNamedCaseAsync("A pinned asset directory allows child publication without allowing directory rename", async () =>
        {
            var container = Directory.CreateDirectory(Path.Combine(root, "owned-container")).FullName;
            var moved = Path.Combine(root, "moved-container");
            using (OwnedFilePublication.DirectoryFence.Open(container, enforceShare: true))
            {
                try { Directory.Move(container, moved); }
                catch (IOException) { }
                Check(Directory.Exists(container) && !Directory.Exists(moved), "asset directory moved while pinned");
                await AssetInventory.ExportAsync(Path.Combine(container, "assets.json"), [asset]);
            }
            Directory.Move(container, moved);
            Check((await AssetInventory.LoadAsync(Path.Combine(moved, "assets.json"))).Single() == asset,
                "directory pin outlived publication or damaged the manifest");
        });
        await Program.RunNamedCaseAsync("Concurrent asset exports publish one complete manifest", async () =>
        {
            //CSJ,variable=destination,格納先
            var destination = Path.Combine(root, "parallel-assets.json");
            //CSJ,variable=gate,排他制御
            using var gate = new Barrier(2);
            //CSJ,variable=timeout,タイムアウト
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            //CSJ,method=ExportAsync,書き出す（非同期）
            async Task<(bool Published, AssetMetadata Expected)> ExportAsync(AssetMetadata expected)
            {
                gate.SignalAndWait(timeout.Token);
                try
                {
                    await AssetInventory.ExportAsync(destination, [expected], timeout.Token);
                    return (true, expected);
                }
                catch (IOException) { return (false, expected); }
            }

            var second = asset with { DownloadUrl = "https://example.invalid/another" };
            //CSJ,variable=results,結果一覧
            var results = await Task.WhenAll(Task.Run(() => ExportAsync(asset)), Task.Run(() => ExportAsync(second)));
            Check(results.Count(entry => entry.Published) == 1 &&
                (await AssetInventory.LoadAsync(destination)).Single() == results.Single(entry => entry.Published).Expected &&
                !Directory.EnumerateFiles(root, "parallel-assets.json.tmp-*").Any(),
                "a conflicting export overwrote an existing result or left its temporary file");
        });
        await File.WriteAllTextAsync(Path.Combine(root, "asset.txt"), "change");
        Check(await AssetInventory.VerifyAsync(root, asset) == AssetVerification.HashMismatch, "hash mismatch");
        //CSJ,variable=project,プロジェクト
        var project = Path.Combine(root, "inventory.ymmp");
        var outside = Path.Combine(workRoot, "outside-media.wav");
        await File.WriteAllTextAsync(outside, "outside");
        await File.WriteAllTextAsync(Path.Combine(root, "present.wav"), "present");
        await File.WriteAllTextAsync(project, JsonSerializer.Serialize(new
        {
            Items = new[] { "asset.txt", "missing.wav", "present.wav", ".\\PRESENT.wav", outside, "NUL.wav", "present.wav:private" }
                .Select(path => new { FilePath = path })
        }));
        //CSJ,variable=scan,走査
        var scan = await AssetInventory.ScanProjectAsync(project, [asset]);
        Check(scan.Count == 6, "equivalent references were not deduplicated");
        Check(scan.Single(item => item.Path == "asset.txt") is { Status: AssetReferenceStatus.Changed, Actual.Size: 6 }
            && scan.Single(item => item.Path == "asset.txt").Expected == asset, "changed reference status");
        Check(scan.Single(item => item.Path == "missing.wav") is { Status: AssetReferenceStatus.Missing, Actual: null }, "missing reference status");
        Check(scan.Single(item => item.Path == "present.wav") is { Status: AssetReferenceStatus.Present, Actual.Size: 7 }, "present reference status");
        Check(scan.Single(item => item.Path == outside) is { Status: AssetReferenceStatus.External, Actual: null }, "external reference was opened");
        Check(scan.Count(item => item.Status == AssetReferenceStatus.Unavailable && item.Actual is null) == 2, "reserved path checks");
        await Program.RunNamedCaseAsync("Inventory marks references under missing directories as missing", async () =>
        {
            //CSJ,variable=nestedProject,入れ子プロジェクト
            var nestedProject = Path.Combine(root, "nested.ymmp");
            //CSJ,variable=relativePath,相対パス
            var relativePath = Path.Combine("missing-folder", "clip.wav");
            await File.WriteAllTextAsync(nestedProject, JsonSerializer.Serialize(new { Items = new[] { new { FilePath = relativePath } } }));
            //CSJ,variable=reference,参照
            var reference = (await AssetInventory.ScanProjectAsync(nestedProject)).Single();
            Check(reference.Status == AssetReferenceStatus.Missing && reference.Actual is null,
                "missing parent was classified as unavailable");
        });
        //CSJ,variable=storage,保管
        var storage = Path.Combine(root, "external-storage");
        Directory.CreateDirectory(storage);
        //CSJ,variable=externalName,外部名前
        var externalName = "external.wav";
        await File.WriteAllTextAsync(Path.Combine(storage, externalName), "external");
        //CSJ,variable=location,場所
        var location = new ExternalAssetLocation("Offline archive", externalName);
        //CSJ,variable=external,外部
        var external = await AssetInventory.InspectExternalAsync(storage, "missing.wav", location, "https://example.invalid/external");
        Check(external.ExternalLocation == location && external.RelativePath == "missing.wav" && external.Size == 8, "external location inspection");
        Check(await AssetInventory.VerifyAsync(root, external) == AssetVerification.ExternalUnverified, "normal verify opened an external asset");
        Check(await AssetInventory.VerifyExternalAsync(storage, external) == AssetVerification.Valid, "approved external root verification");
        Check(await AssetInventory.VerifyExternalAsync(root, external) == AssetVerification.Missing, "wrong external root verification");
        await File.WriteAllTextAsync(Path.Combine(storage, externalName), "different");
        Check(await AssetInventory.VerifyExternalAsync(storage, external) == AssetVerification.SizeMismatch, "external change verification");
        await Program.RunNamedCaseAsync("External batch continues after an unreadable asset", async () =>
        {
            await File.WriteAllTextAsync(Path.Combine(storage, externalName), "external");
            //CSJ,variable=missing,欠落
            var missing = external with { ExternalLocation = location with { RelativePath = "missing.wav" } };
            var lockedPath = Path.Combine(storage, "locked.wav");
            await File.WriteAllTextAsync(lockedPath, "external");
            var locked = external with { ExternalLocation = location with { RelativePath = "locked.wav" } };
            await using var held = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);
            //CSJ,variable=results,結果一覧
            var results = await AssetInventory.VerifyExternalBatchAsync(storage, [locked, missing, external], default);
            Check(results.SequenceEqual(new[] { AssetVerification.Unreadable, AssetVerification.Missing, AssetVerification.Valid }),
                "batch did not retain per-item results in order");
            //CSJ,variable=cancellation,キャンセル
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                await AssetInventory.VerifyExternalBatchAsync(storage, [locked, missing, external], cancellation.Token);
                throw new InvalidOperationException("batch ignored cancellation");
            }
            catch (OperationCanceledException) { }
        });
        await Program.RunNamedCaseAsync("External verification distinguishes missing directories from unreadable files", async () =>
        {
            //CSJ,variable=results,結果一覧
            var results = await AssetInventory.VerifyExternalBatchAsync(Path.Combine(root, "missing-root"), [external], default);
            Check(results.Single() == AssetVerification.Missing, "missing approved root was not classified as missing");
            //CSJ,variable=nested,入れ子
            var nested = external with { ExternalLocation = location with { RelativePath = "missing-folder/asset.wav" } };
            results = await AssetInventory.VerifyExternalBatchAsync(storage, [nested, external], default);
            Check(results.SequenceEqual(new[] { AssetVerification.Missing, AssetVerification.Valid }),
                "missing asset parent changed the result of another asset");
        });
        var withStorage = await AssetInventory.ScanProjectAsync(project, [external]);
        Check(withStorage.Single(item => item.Path == "missing.wav") is { Status: AssetReferenceStatus.External, Actual: null }
            && withStorage.Single(item => item.Path == "missing.wav").Expected == external, "scan did not expose configured external storage");
        //CSJ,variable=externalOutput,外部出力
        var externalOutput = Path.Combine(root, "external-metadata.json");
        await AssetInventory.ExportAsync(externalOutput, [external]);
        //CSJ,variable=externalJson,外部JSON
        var externalJson = await File.ReadAllTextAsync(externalOutput);
        Check((await AssetInventory.LoadAsync(externalOutput)).Single() == external &&
            !externalJson.Contains(storage, StringComparison.OrdinalIgnoreCase), "external metadata did not round-trip privately");
        Check(!JsonSerializer.Serialize(asset).Contains(nameof(AssetMetadata.ExternalLocation), StringComparison.Ordinal),
            "legacy local serialization changed");
        try
        {
            await AssetInventory.InspectExternalAsync(storage, "missing.wav", new ExternalAssetLocation("Offline archive", "..\\outside-media.wav"));
            throw new Exception("external storage path traversal accepted");
        }
        catch (InvalidDataException) { }
        File.Delete(Path.Combine(root, "asset.txt"));
        Check(await AssetInventory.VerifyAsync(root, asset) == AssetVerification.Missing, "missing asset");
        try { await AssetInventory.InspectAsync(root, "../outside"); throw new Exception("traversal accepted"); } catch (ArgumentException) { }
        try { await AssetInventory.InspectAsync(root, "missing", "https://user:password@example.invalid/a"); throw new Exception("credentials accepted"); } catch (ArgumentException) { }
        //CSJ,variable=cancelled,キャンセル済み
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        //CSJ,variable=cancelledOutput,キャンセル済み出力
        var cancelledOutput = Path.Combine(root, "cancelled.json");
        try { await AssetInventory.ExportAsync(cancelledOutput, [asset], cancelled.Token); throw new Exception("cancel ignored"); } catch (OperationCanceledException) { }
        Check(!File.Exists(cancelledOutput), "cancelled export left output");
        var real = Path.Combine(root, "real"); var realProject = Path.Combine(real, "project");
        var alias = Path.Combine(root, "alias"); Directory.CreateDirectory(realProject);
        await File.WriteAllTextAsync(Path.Combine(realProject, "asset.txt"), "synthetic");
        var junction = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/d", "/c", "mklink", "/J", alias, real }) junction.ArgumentList.Add(arg);
        //CSJ,variable=process,プロセス
        using (var process = Process.Start(junction)!)
        {
            //CSJ,variable=stdout,標準出力
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
