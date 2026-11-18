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
        var root = Path.Combine(workRoot, "ymmpx-archive-preflight"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "unsafe.ymmpx");
        using (var bytes = Archive("../outside.txt")) await File.WriteAllBytesAsync(path, bytes.ToArray());
        var bridge = new YmmpxLibBridge(new YMMProjectManager.Infrastructure.FileLogger(Path.Combine(root, "test.log")), typeof(YmmpxLib.YmmpxPackageService).Assembly);
        var before = YmmpxLib.YmmpxPackageService.ExtractionCalls;
        var result = await bridge.ExtractPackageAsync(path, root, CancellationToken.None);
        if (result.Success || YmmpxLib.YmmpxPackageService.ExtractionCalls != before) throw new Exception("unsafe archive reached extractor");
        Console.WriteLine("YMMPX archive safety preflight checks passed.");
    }
}
