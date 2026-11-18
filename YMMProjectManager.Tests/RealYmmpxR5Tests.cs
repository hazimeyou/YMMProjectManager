using System.IO;
using System.IO.Compression;
using System.Reflection;
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
        var blocked = await service.ExtractPackageAsync(output, Path.Combine(root, "bridge-extract"), CancellationToken.None);
        if (blocked.Success || Directory.Exists(Path.Combine(root, "bridge-extract"))) throw new Exception("bridge extraction was not blocked");
        Console.WriteLine("R5 real YmmpxLib: bridge creation and isolated direct-library extraction passed; bridge extraction remains blocked.");
    }
}
