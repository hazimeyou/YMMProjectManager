using System.IO;
using System.IO.Compression;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Packaging;

internal static class YmmpxFailureTests
{
    public static async Task RunAsync(string workRoot)
    {
        var root = Path.Combine(workRoot, "ymmpx-failure"); Directory.CreateDirectory(root);
        var project = Path.Combine(root, "sample.ymmp"); var output = Path.Combine(root, "sample.ymmpx");
        await File.WriteAllTextAsync(project, "{}"); await File.WriteAllTextAsync(output, "original package");
        var bridge = new YmmpxLibBridge(new FileLogger(Path.Combine(root, "test.log")), typeof(YmmpxLib.YmmpxPackageService).Assembly);
        var refusal = await bridge.CreatePackageAsync(project, output, CancellationToken.None);
        if (refusal.Success || !refusal.ErrorMessage!.Contains("別の名前") || await File.ReadAllTextAsync(output) != "original package")
            throw new Exception("existing package was not protected");
        var newPath = Path.Combine(root, "new.ymmpx");
        var result = await bridge.CreatePackageAsync(project, newPath, CancellationToken.None);
        if (result.Success || File.Exists(newPath)) throw new Exception("partial package was published");
        YmmpxLib.YmmpxPackageService.Mode = "invalid";
        var invalid = await bridge.CreatePackageAsync(project, newPath, CancellationToken.None);
        if (invalid.Success || File.Exists(newPath)) throw new Exception("invalid archive was published");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await bridge.CreatePackageAsync(project, newPath, cancelled.Token); throw new Exception("cancellation ignored"); } catch (OperationCanceledException) { }
            if (File.Exists(newPath)) throw new Exception("cancelled operation created package");
        }
        YmmpxLib.YmmpxPackageService.Mode = "success";
        YmmpxLib.YmmpxPackageService.ExternalPath = newPath;
        var conflict = await bridge.CreatePackageAsync(project, newPath, CancellationToken.None);
        if (conflict.Success || await File.ReadAllTextAsync(newPath) != "external edit")
            throw new Exception("concurrent destination overwritten");
        YmmpxLib.YmmpxPackageService.ExternalPath = null;
        var safePath = Path.Combine(root, "save-as.ymmpx");
        var created = await bridge.CreatePackageAsync(project, safePath, CancellationToken.None);
        if (!created.Success || created.OutputPath != safePath || !IsPackage(safePath)) throw new Exception("new package failed");
        using (var held = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var locked = await bridge.CreatePackageAsync(project, output, CancellationToken.None);
            if (locked.Success) throw new Exception("locked package replaced");
        }
        if (await File.ReadAllTextAsync(output) != "original package") throw new Exception("existing package changed");
        if (Directory.EnumerateFiles(root, ".*.tmp.ymmpx").Any()) throw new Exception("temporary package remained");
        Console.WriteLine("YMMPX synthetic partial write preserves existing package.");
    }

    private static bool IsPackage(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.GetEntry("synthetic.txt") is not null;
    }
}

namespace YmmpxLib
{
    public sealed class YmmpxPackagingOptions { public bool IncludeProjectUiSettings { get; set; } }
    public sealed class YmmpxPackagingProgress { public int Percentage { get; set; } }
    public static class YmmpxPackageService
    {
        public static string Mode { get; set; } = "failure";
        public static string? ExternalPath { get; set; }
        public static int ExtractionCalls { get; private set; }
        public static string GetAvailableDirectoryPath(string path) => path;
        public static object ExtractAndRestoreProject(string path, string destination)
        {
            ExtractionCalls++;
            throw new InvalidOperationException("synthetic extraction must not be called for unsafe archives");
        }
        public static async Task<object> CreatePackageAsync(string project, string output, HashSet<string> ignored, YmmpxPackagingOptions options, IProgress<YmmpxPackagingProgress>? progress, CancellationToken token)
        {
            if (Mode == "failure")
            {
                await File.WriteAllTextAsync(output, "partial", token);
                throw new IOException("synthetic partial write");
            }
            if (Mode == "invalid")
            {
                await File.WriteAllTextAsync(output, "invalid archive", token);
                return new { OutputPath = output, ResourceCount = 0 };
            }
            using (var stream = new FileStream(output, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("synthetic.txt").Open()))
                await writer.WriteAsync("synthetic package");
            if (ExternalPath is not null) await File.WriteAllTextAsync(ExternalPath, "external edit", token);
            return new { OutputPath = output, ResourceCount = 1 };
        }
    }
}
