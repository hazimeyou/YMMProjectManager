using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Packaging;

//CSJ,class=RealYmmpxR5Tests,実際のYMMPXを使うR5テスト
internal static class RealYmmpxR5Tests
{
    //CSJ,method=RunAsync,実行する（非同期）
    public static async Task RunAsync(string workRoot)
    {
        //CSJ,variable=library,ライブラリ
        var library = Environment.GetEnvironmentVariable("YMMPX_LIB_PATH");
        if (string.IsNullOrWhiteSpace(library) || !File.Exists(library)) { Program.ReportConditionalSkip("R5 real YmmpxLib unavailable"); return; }
        await R30ReflectionTests.RunRealAsync(workRoot, library);
        //CSJ,variable=root,ルート
        var root = Path.Combine(workRoot, "r5-real-ymmpx"); Directory.CreateDirectory(root);
        //CSJ,variable=project,プロジェクト
        var project = Path.Combine(root, "project.ymmp"); var asset = Path.Combine(root, "asset.png");
        await File.WriteAllTextAsync(asset, "synthetic media");
        await File.WriteAllTextAsync(project, "{\"Items\":[{\"FilePath\":\"asset.png\"}]}");
        //CSJ,variable=service,サービス
        var service = new YmmpxLibBundleService(new FileLogger(Path.Combine(root, "test.log")), library, root);
        var output = Path.Combine(root, "package.ymmpx");
        //CSJ,variable=created,作成済み
        var created = await service.CreatePackageAsync(project, output, CancellationToken.None);
        if (!created.Success || !File.Exists(output)) throw new Exception("real library package creation failed: " + created.ErrorMessage);
        //CSJ,variable=archive,アーカイブ
        using (var archive = ZipFile.OpenRead(output))
            if (archive.GetEntry("links.json") is null || archive.GetEntry("_ymmpx_project_path.txt") is null) throw new Exception("real package entries missing");
        //CSJ,variable=bytes,バイト列
        var bytes = await File.ReadAllBytesAsync(output);
        //CSJ,variable=duplicate,重複
        var duplicate = await service.CreatePackageAsync(project, output, CancellationToken.None);
        var after = await File.ReadAllBytesAsync(output);
        if (duplicate.Success || !bytes.SequenceEqual(after)) throw new Exception("existing package changed");
        //CSJ,variable=assembly,アセンブリ
        var assembly = Assembly.LoadFrom(library);
        //CSJ,variable=method,メソッド
        var method = assembly.GetType("YmmpxLib.YmmpxPackageService")!.GetMethod("ExtractAndRestoreProject")!;
        //CSJ,variable=destination,格納先
        var destination = Path.Combine(root, "direct-extract");
        var extracted = method.Invoke(null, [output, destination]) ?? throw new Exception("direct extraction failed");
        //CSJ,variable=restored,復元済み
        var restored = extracted.GetType().GetProperty("ProjectFilePath")!.GetValue(extracted)?.ToString();
        if (restored is null || !Path.GetFullPath(restored).StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(restored))
            throw new Exception("direct extraction result outside isolated destination");
        //CSJ,variable=managed,管理対象
        var managed = await service.ExtractPackageAsync(output, Path.Combine(root, "bridge-extract"), CancellationToken.None);
        if (!managed.Success || managed.ReplacedPathCount < 1 || !File.Exists(managed.RestoredProjectPath))
            throw new Exception("managed reader could not extract the real package");
        await Program.RunNamedCaseAsync("Concurrent extraction of a real YMMPX publishes once", async () =>
        {
            //CSJ,variable=sharedDestination,共有格納先
            var sharedDestination = Path.Combine(root, "shared-extract");
            using var bothWriting = new Barrier(2);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            InlineProgress FirstWrite() => new(() => bothWriting.SignalAndWait(deadline.Token));
            var first = service.ExtractPackageAsync(output, sharedDestination, deadline.Token, FirstWrite());
            var second = service.ExtractPackageAsync(output, sharedDestination, deadline.Token, FirstWrite());
            //CSJ,variable=results,結果一覧
            var results = await Task.WhenAll(first, second);
            if (results.Count(result => result.Success) != 1)
                throw new InvalidOperationException("One concurrent extraction must publish and the other must stop.");
            var winner = results.Single(result => result.Success);
            //CSJ,variable=restoredJson,復元済みJSON
            using var restoredJson = JsonDocument.Parse(await File.ReadAllTextAsync(winner.RestoredProjectPath!));
            //CSJ,variable=media,メディア
            var media = restoredJson.RootElement.GetProperty("Items")[0].GetProperty("FilePath").GetString();
            //CSJ,variable=packageAfter,パッケージ変更後
            var packageAfter = await File.ReadAllBytesAsync(output);
            if (winner.ExtractedResourceCount != managed.ExtractedResourceCount ||
                media is null || !media.StartsWith(sharedDestination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                await File.ReadAllTextAsync(media) != "synthetic media" ||
                Directory.EnumerateDirectories(root, ".ymmpx-*").Any() || !bytes.SequenceEqual(packageAfter))
                throw new InvalidOperationException("Concurrent extraction changed the package or left an incomplete destination.");
        });
        await Program.RunNamedCaseAsync("Cancelled real YMMPX extraction can retry the same destination", async () =>
        {
            //CSJ,variable=retryDestination,再試行格納先
            var retryDestination = Path.Combine(root, "retry-extract");
            //CSJ,variable=cancelled,キャンセル済み
            using var cancelled = new CancellationTokenSource();
            try
            {
                await service.ExtractPackageAsync(output, retryDestination, cancelled.Token, new InlineProgress(cancelled.Cancel));
                throw new InvalidOperationException("The cancelled extraction published output.");
            }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }

            if (Directory.Exists(retryDestination) || Directory.EnumerateDirectories(root, ".ymmpx-*").Any())
                throw new InvalidOperationException("The cancelled extraction left its destination or working directory.");

            var retried = await service.ExtractPackageAsync(output, retryDestination, CancellationToken.None);
            //CSJ,variable=packageAfter,パッケージ変更後
            var packageAfter = await File.ReadAllBytesAsync(output);
            if (!retried.Success || !File.Exists(retried.RestoredProjectPath) || !bytes.SequenceEqual(packageAfter))
                throw new InvalidOperationException("Retry could not publish without changing the original package.");
        });
        await Program.RunNamedCaseAsync("Real YMMPX preserves Unicode names and unrelated project fields", async () =>
        {
            //CSJ,variable=namedProject,名前付きプロジェクト
            var namedProject = Path.Combine(root, "場面 名前.ymmp");
            var original = """{"Items":[{"FilePath":"asset.png","Caption":"日本語"}],"FutureData":{"Revision":7,"Labels":["alpha","絵"]}}""";
            await File.WriteAllTextAsync(namedProject, original);
            //CSJ,variable=namedPackage,名前付きパッケージ
            var namedPackage = Path.Combine(root, "別の出力.ymmpx");
            //CSJ,variable=packageResult,パッケージ結果
            var packageResult = await service.CreatePackageAsync(namedProject, namedPackage, default);
            if (!packageResult.Success) throw new InvalidOperationException(packageResult.ErrorMessage);
            //CSJ,variable=extraction,抽出
            var extraction = await service.ExtractPackageAsync(namedPackage, Path.Combine(root, "unicode-extract"), default);
            if (!extraction.Success || Path.GetFileName(extraction.RestoredProjectPath) != "場面 名前.ymmp")
                throw new InvalidOperationException("Package did not preserve the selected project name.");
            //CSJ,variable=restored,復元済み
            using var restored = JsonDocument.Parse(await File.ReadAllTextAsync(extraction.RestoredProjectPath!));
            using var source = JsonDocument.Parse(original);
            if (!JsonElement.DeepEquals(source.RootElement.GetProperty("FutureData"), restored.RootElement.GetProperty("FutureData")) ||
                restored.RootElement.GetProperty("Items")[0].GetProperty("Caption").GetString() != "日本語" ||
                await File.ReadAllTextAsync(namedProject) != original)
                throw new InvalidOperationException("Packaging changed the source or lost unrelated data.");
        });
        await Program.RunNamedCaseAsync("Real YMMPX keeps project shape while rewriting material paths", async () =>
        {
            //CSJ,variable=nested,入れ子
            var nested = Path.Combine(root, "nested");
            Directory.CreateDirectory(nested);
            await File.WriteAllTextAsync(Path.Combine(nested, "asset.png"), "nested media");
            //CSJ,variable=pathProject,パスプロジェクト
            var pathProject = Path.Combine(root, "path-kinds.ymmp");
            var original = JsonSerializer.Serialize(new
            {
                Items = new object[]
                {
                    new { FilePath = "asset.png", Volume = 1 },
                    new { FilePath = "nested/asset.png", Volume = 2 },
                    new { FilePath = new Uri(asset).AbsoluteUri, Volume = 3 },
                    new { FilePath = "missing.png", Volume = 4 },
                    new { FilePath = asset, Volume = 5 },
                    new { FilePath = "", Volume = 6 },
                    new { FilePath = (string?)null, Volume = 7 },
                },
                Unrelated = new { Rate = 100.0, Enabled = true },
            });
            await File.WriteAllTextAsync(pathProject, original);
            //CSJ,variable=destination,格納先
            var destination = Path.Combine(root, "paths.ymmpx");
            var result = await service.CreatePackageAsync(pathProject, destination, default);
            if (!result.Success) throw new InvalidOperationException(result.ErrorMessage);
            //CSJ,variable=archive,アーカイブ
            using var archive = ZipFile.OpenRead(destination);
            //CSJ,variable=reader,読み取り元
            using var reader = new StreamReader(archive.GetEntry("path-kinds.ymmp")!.Open());
            using var packaged = JsonDocument.Parse(await reader.ReadToEndAsync());
            using var source = JsonDocument.Parse(original);
            var projectItems = packaged.RootElement.GetProperty("Items");
            var sourceItems = source.RootElement.GetProperty("Items");
            //CSJ,variable=sharedResource,共有リソース
            var sharedResource = projectItems[0].GetProperty("FilePath").GetString();
            //CSJ,variable=nestedResource,入れ子リソース
            var nestedResource = projectItems[1].GetProperty("FilePath").GetString();
            if (!JsonElement.DeepEquals(source.RootElement.GetProperty("Unrelated"), packaged.RootElement.GetProperty("Unrelated")) ||
                projectItems.GetArrayLength() != sourceItems.GetArrayLength() ||
                Enumerable.Range(0, sourceItems.GetArrayLength()).Any(index =>
                    projectItems[index].GetProperty("Volume").GetInt32() != sourceItems[index].GetProperty("Volume").GetInt32()) ||
                sharedResource is null || nestedResource is null || sharedResource == nestedResource ||
                archive.GetEntry(sharedResource) is null || archive.GetEntry(nestedResource) is null ||
                projectItems[2].GetProperty("FilePath").GetString() != sharedResource ||
                projectItems[4].GetProperty("FilePath").GetString() != sharedResource ||
                projectItems[3].GetProperty("FilePath").GetString() != "missing.png" ||
                projectItems[5].GetProperty("FilePath").GetString() != "" ||
                projectItems[6].GetProperty("FilePath").ValueKind != JsonValueKind.Null)
                throw new InvalidOperationException("Current-form packaging did not preserve non-resource values or expected material links.");
            using var first = new StreamReader(archive.GetEntry(sharedResource)!.Open());
            using var second = new StreamReader(archive.GetEntry(nestedResource)!.Open());
            if (await first.ReadToEndAsync() != await File.ReadAllTextAsync(asset) ||
                await second.ReadToEndAsync() != "nested media")
                throw new InvalidOperationException("Current-form packaging mixed the contents of resources that share a basename.");
        });
        Console.WriteLine("Real YmmpxLib package creation, direct-library extraction and managed product extraction passed.");
    }

    //CSJ,class=InlineProgress,インライン進捗
    private sealed class InlineProgress(Action firstWrite) : IProgress<double>
    {
        private bool reported;

        public void Report(double value)
        {
            if (reported) return;
            reported = true;
            firstWrite();
        }
    }
}
