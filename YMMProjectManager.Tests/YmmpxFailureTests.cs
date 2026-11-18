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
        var result = await bridge.CreatePackageAsync(project, output, CancellationToken.None);
        if (result.Success || await File.ReadAllTextAsync(output) != "original package")
            throw new Exception("existing package was not preserved after library failure");
        YmmpxLib.YmmpxPackageService.Mode = "invalid";
        var invalid = await bridge.CreatePackageAsync(project, output, CancellationToken.None);
        if (invalid.Success || await File.ReadAllTextAsync(output) != "original package")
            throw new Exception("invalid archive replaced existing package");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await bridge.CreatePackageAsync(project, output, cancelled.Token); throw new Exception("cancellation ignored"); } catch (OperationCanceledException) { }
            if (await File.ReadAllTextAsync(output) != "original package") throw new Exception("cancelled operation changed package");
        }
        YmmpxLib.YmmpxPackageService.Mode = "success";
        var saved = await bridge.CreatePackageAsync(project, output, CancellationToken.None);
        if (!saved.Success || saved.OutputPath != output || !IsPackage(output))
            throw new Exception("package replacement failed");
        var beforeLock = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(output));
        using (var held = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var locked = await bridge.CreatePackageAsync(project, output, CancellationToken.None);
            if (locked.Success) throw new Exception("locked package replaced");
        }
        var afterLock = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(output));
        if (!beforeLock.SequenceEqual(afterLock))
            throw new Exception("locked package changed");
        YmmpxLib.YmmpxPackageService.ExternalPath = output;
        var conflict = await bridge.CreatePackageAsync(project, output, CancellationToken.None);
        if (conflict.Success || await File.ReadAllTextAsync(output) != "external edit")
            throw new Exception("external modification overwritten");
        YmmpxLib.YmmpxPackageService.ExternalPath = null;
        var newPath = Path.Combine(root, "new.ymmpx");
        var created = await bridge.CreatePackageAsync(project, newPath, CancellationToken.None);
        if (!created.Success || !IsPackage(newPath)) throw new Exception("new package failed");
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
