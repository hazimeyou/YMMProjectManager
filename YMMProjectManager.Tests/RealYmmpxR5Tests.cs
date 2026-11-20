using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Packaging;

internal static class RealYmmpxR5Tests
{
    public static async Task RunAsync(string workRoot)
    {
        var library = Environment.GetEnvironmentVariable("YMMPX_LIB_PATH");
        if (string.IsNullOrWhiteSpace(library) || !File.Exists(library)) { Program.ReportConditionalSkip("R5 real YmmpxLib unavailable"); return; }
        var root = Path.Combine(workRoot, "r5-real-ymmpx"); Directory.CreateDirectory(root);
        var project = Path.Combine(root, "project.ymmp"); var asset = Path.Combine(root, "asset.png");
        await File.WriteAllTextAsync(asset, "synthetic media");
        await File.WriteAllTextAsync(project, "{\"Items\":[{\"FilePath\":\"asset.png\"}]}");
        var service = new YmmpxLibBundleService(new FileLogger(Path.Combine(root, "test.log")), library, root);
        var output = Path.Combine(root, "package.ymmpx");
        var created = await service.CreatePackageAsync(project, output, CancellationToken.None);
        if (!created.Success || !File.Exists(output)) throw new Exception("real library package creation failed: " + created.ErrorMessage);
        using (var archive = ZipFile.OpenRead(output))
            if (archive.GetEntry("links.json") is null || archive.GetEntry("_ymmpx_project_path.txt") is null) throw new Exception("real package entries missing");
        var bytes = await File.ReadAllBytesAsync(output);
        var duplicate = await service.CreatePackageAsync(project, output, CancellationToken.None);
        var after = await File.ReadAllBytesAsync(output);
        if (duplicate.Success || !bytes.SequenceEqual(after)) throw new Exception("existing package changed");
        var assembly = Assembly.LoadFrom(library);
        var method = assembly.GetType("YmmpxLib.YmmpxPackageService")!.GetMethod("ExtractAndRestoreProject")!;
        var destination = Path.Combine(root, "direct-extract");
        var extracted = method.Invoke(null, [output, destination]) ?? throw new Exception("direct extraction failed");
        var restored = extracted.GetType().GetProperty("ProjectFilePath")!.GetValue(extracted)?.ToString();
        if (restored is null || !Path.GetFullPath(restored).StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(restored))
            throw new Exception("direct extraction result outside isolated destination");
        var managed = await service.ExtractPackageAsync(output, Path.Combine(root, "bridge-extract"), CancellationToken.None);
        if (!managed.Success || managed.ReplacedPathCount < 1 || !File.Exists(managed.RestoredProjectPath))
            throw new Exception("managed reader could not extract the real package");
        await Program.RunNamedCaseAsync("Concurrent extraction of a real YMMPX publishes once", async () =>
        {
            var sharedDestination = Path.Combine(root, "shared-extract");
            using var bothWriting = new Barrier(2);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            InlineProgress FirstWrite() => new(() => bothWriting.SignalAndWait(deadline.Token));
            var first = service.ExtractPackageAsync(output, sharedDestination, deadline.Token, FirstWrite());
            var second = service.ExtractPackageAsync(output, sharedDestination, deadline.Token, FirstWrite());
            var results = await Task.WhenAll(first, second);
            if (results.Count(result => result.Success) != 1)
                throw new InvalidOperationException("One concurrent extraction must publish and the other must stop.");
            var winner = results.Single(result => result.Success);
            using var restoredJson = JsonDocument.Parse(await File.ReadAllTextAsync(winner.RestoredProjectPath!));
            var media = restoredJson.RootElement.GetProperty("Items")[0].GetProperty("FilePath").GetString();
            var packageAfter = await File.ReadAllBytesAsync(output);
            if (winner.ExtractedResourceCount != managed.ExtractedResourceCount ||
                media is null || !media.StartsWith(sharedDestination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                await File.ReadAllTextAsync(media) != "synthetic media" ||
                Directory.EnumerateDirectories(root, ".ymmpx-*").Any() || !bytes.SequenceEqual(packageAfter))
                throw new InvalidOperationException("Concurrent extraction changed the package or left an incomplete destination.");
        });
        await Program.RunNamedCaseAsync("Cancelled real YMMPX extraction can retry the same destination", async () =>
        {
            var retryDestination = Path.Combine(root, "retry-extract");
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
            var packageAfter = await File.ReadAllBytesAsync(output);
            if (!retried.Success || !File.Exists(retried.RestoredProjectPath) || !bytes.SequenceEqual(packageAfter))
                throw new InvalidOperationException("Retry could not publish without changing the original package.");
        });
        await Program.RunNamedCaseAsync("Real YMMPX preserves Unicode names and unrelated project fields", async () =>
        {
            var namedProject = Path.Combine(root, "場面 名前.ymmp");
            var original = """{"Items":[{"FilePath":"asset.png","Caption":"日本語"}],"FutureData":{"Revision":7,"Labels":["alpha","絵"]}}""";
            await File.WriteAllTextAsync(namedProject, original);
            var namedPackage = Path.Combine(root, "別の出力.ymmpx");
            var packageResult = await service.CreatePackageAsync(namedProject, namedPackage, default);
            if (!packageResult.Success) throw new InvalidOperationException(packageResult.ErrorMessage);
            var extraction = await service.ExtractPackageAsync(namedPackage, Path.Combine(root, "unicode-extract"), default);
            if (!extraction.Success || Path.GetFileName(extraction.RestoredProjectPath) != "場面 名前.ymmp")
                throw new InvalidOperationException("Package did not preserve the selected project name.");
            using var restored = JsonDocument.Parse(await File.ReadAllTextAsync(extraction.RestoredProjectPath!));
            using var source = JsonDocument.Parse(original);
            if (!JsonElement.DeepEquals(source.RootElement.GetProperty("FutureData"), restored.RootElement.GetProperty("FutureData")) ||
                restored.RootElement.GetProperty("Items")[0].GetProperty("Caption").GetString() != "日本語" ||
                await File.ReadAllTextAsync(namedProject) != original)
                throw new InvalidOperationException("Packaging changed the source or lost unrelated data.");
        });
        Console.WriteLine("Real YmmpxLib package creation, direct-library extraction and managed product extraction passed.");
    }

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
