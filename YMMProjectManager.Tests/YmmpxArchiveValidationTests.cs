using System.IO;
using System.IO.Compression;
using YMMProjectManager.Infrastructure.Packaging;

internal static class YmmpxArchiveValidationTests
{
    public static async Task RunAsync(string workRoot)
    {
        static MemoryStream Archive(params string[] names)
        {
            var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var name in names)
                    using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write("synthetic");
            stream.Position = 0;
            return stream;
        }
        static void Rejected(params string[] names)
        {
            using var stream = Archive(names);
            try { YmmpxLibBridge.ValidateArchiveForExtraction(stream, CancellationToken.None); throw new Exception("unsafe archive accepted"); }
            catch (InvalidDataException) { }
        }
        using (var safe = Archive("resources/a.txt", "project.ymmp")) YmmpxLibBridge.ValidateArchiveForExtraction(safe, CancellationToken.None);
        Rejected("../outside.txt"); Rejected("/absolute.txt"); Rejected("C:/outside.txt");
        Rejected("resources/Folder/a.txt", "resources/folder/b.txt");
        Rejected("resources/Folder/a.txt", "resources/folder/nested/b.txt");
        Rejected("folder/a.txt", "FOLDER/A.TXT"); Rejected("file", "file/child"); Rejected("NUL.txt"); Rejected("file.txt:stream");
        using (var oversized = Archive("file.txt"))
        {
            try { YmmpxLibBridge.ValidateArchiveForExtraction(oversized, CancellationToken.None, maxTotalBytes: 2); throw new Exception("oversize archive accepted"); }
            catch (InvalidDataException) { }
        }
        using (var cancelled = new CancellationTokenSource())
        using (var archive = Archive("file.txt"))
        {
            cancelled.Cancel();
            try { YmmpxLibBridge.ValidateArchiveForExtraction(archive, cancelled.Token); throw new Exception("cancellation ignored"); }
            catch (OperationCanceledException) { }
        }
        using (var cancelled = new CancellationTokenSource())
        using (var emptyArchive = Archive())
        {
            cancelled.Cancel();
            try { YmmpxLibBridge.ValidateArchiveForExtraction(emptyArchive, cancelled.Token); throw new Exception("empty archive cancellation ignored"); }
            catch (OperationCanceledException) { }
        }
        var root = Path.Combine(workRoot, "ymmpx-archive-preflight"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "unsafe.ymmpx");
        using (var bytes = Archive("../outside.txt")) await File.WriteAllBytesAsync(path, bytes.ToArray());
        var bridge = new YmmpxLibBridge(new YMMProjectManager.Infrastructure.FileLogger(Path.Combine(root, "test.log")), typeof(YmmpxLib.YmmpxPackageService).Assembly);
        var before = YmmpxLib.YmmpxPackageService.ExtractionCalls;
        var result = await bridge.ExtractPackageAsync(path, Path.Combine(root, "rejected-extraction"), CancellationToken.None);
        if (result.Success || YmmpxLib.YmmpxPackageService.ExtractionCalls != before) throw new Exception("unsafe archive reached extractor");
        using (var bytes = Archive("project.ymmp")) await File.WriteAllBytesAsync(path, bytes.ToArray());
        var destination = Path.Combine(root, "restored");
        var unsupported = await bridge.ExtractPackageAsync(path, destination, CancellationToken.None);
        if (unsupported.Success || Directory.Exists(destination)) throw new Exception("unverified format was published");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            static void Write(ZipArchive zip, string name, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
            Write(zip, "_ymmpx_project_path.txt", "project.ymmp");
            Write(zip, "links.json", """{"resources/clip.txt":"resources/clip.txt"}""");
            Write(zip, "project.ymmp", """{"Items":[{"FilePath":"resources/clip.txt"}],"Custom":42}""");
            Write(zip, "resources/clip.txt", "synthetic asset");
        }
        var recovered = await bridge.ExtractPackageAsync(path, destination, CancellationToken.None);
        if (!recovered.Success || recovered.ReplacedPathCount != 1 || recovered.ExtractedResourceCount != 1)
            throw new Exception("managed extraction failed: " + recovered.ErrorMessage);
        using var project = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(recovered.RestoredProjectPath!));
        var resource = project.RootElement.GetProperty("Items")[0].GetProperty("FilePath").GetString();
        if (resource != Path.Combine(destination, "resources", "clip.txt") || await File.ReadAllTextAsync(resource) != "synthetic asset" ||
            project.RootElement.GetProperty("Custom").GetInt32() != 42) throw new Exception("reference or project field mismatch");
        if ((await bridge.ExtractPackageAsync(path, destination, CancellationToken.None)).Success ||
            YmmpxLib.YmmpxPackageService.ExtractionCalls != before)
            throw new Exception("managed extraction overwrote existing output or invoked external extractor");
        var cancelledOutput = Path.Combine(root, "cancelled");
        using (var cancelDuringCopy = new CancellationTokenSource())
        {
            try
            {
                await bridge.ExtractPackageAsync(path, cancelledOutput, cancelDuringCopy.Token, new InlineProgress(() => cancelDuringCopy.Cancel()));
                throw new Exception("mid-extraction cancellation was ignored");
            }
            catch (OperationCanceledException) { }
        }
        if (Directory.Exists(cancelledOutput) || Directory.EnumerateDirectories(root, ".ymmpx-*").Any())
            throw new Exception("cancelled extraction leaked output");
        var raced = Path.Combine(root, "raced");
        var race = await bridge.ExtractPackageAsync(path, raced, CancellationToken.None, new InlineProgress(() =>
        {
            Directory.CreateDirectory(raced);
            File.WriteAllText(Path.Combine(raced, "sentinel.txt"), "existing");
        }));
        if (race.Success || await File.ReadAllTextAsync(Path.Combine(raced, "sentinel.txt")) != "existing" ||
            Directory.EnumerateDirectories(root, ".ymmpx-*").Any())
            throw new Exception("destination race did not preserve existing output");
        Console.WriteLine("YMMPX archive safety preflight checks passed.");
    }

    private sealed class InlineProgress(Action action) : IProgress<double>
    {
        public void Report(double value) => action();
    }
}
