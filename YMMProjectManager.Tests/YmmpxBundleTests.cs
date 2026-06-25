using System.IO;
using System.IO.Compression;
using System.Text.Json;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Packaging;

internal static class YmmpxBundleTests
{
    public static async Task RunAsync(string workRoot)
    {
        await TestResolverSearchPathsAsync(workRoot);
        await TestMissingYmmpxLibReturnsErrorAsync(workRoot);

        var ymmpxLibPath = LocateYmmpxLibDll();
        if (!string.IsNullOrWhiteSpace(ymmpxLibPath))
        {
            await TestPackageAndExtractWithYmmpxLibAsync(workRoot, ymmpxLibPath);
        }
    }

    private static Task TestResolverSearchPathsAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestResolverSearchPathsAsync));
        var searchPaths = YmmpxLibResolver.GetSearchPaths(root, Path.Combine(root, "custom", "YmmpxLib.dll"));

        AssertTrue(searchPaths.Any(path => path.EndsWith(Path.Combine("user", "plugin", "YmmpxLibPlugin", "YmmpxLib.dll"), StringComparison.OrdinalIgnoreCase)), "search paths should include the YMM4 plugin path");
        AssertTrue(searchPaths.Contains(Path.Combine(root, "custom", "YmmpxLib.dll"), StringComparer.OrdinalIgnoreCase), "search paths should include explicit override");
        return Task.CompletedTask;
    }

    private static async Task TestMissingYmmpxLibReturnsErrorAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestMissingYmmpxLibReturnsErrorAsync));
        var logger = new FileLogger(Path.Combine(root, "logs", "missing.log"));
        var service = new YmmpxLibBundleService(logger, explicitAssemblyPath: Path.Combine(root, "missing", "YmmpxLib.dll"), baseDirectory: root);

        var projectPath = Path.Combine(root, "sample.ymmp");
        await File.WriteAllTextAsync(projectPath, """
        { "FilePath": "asset.png" }
        """);

        var result = await service.CreatePackageAsync(projectPath, Path.Combine(root, "sample.ymmpx"), CancellationToken.None);

        AssertTrue(!result.Success, "package creation should fail when YmmpxLib is missing");
        AssertTrue(result.ErrorMessage?.Contains("YmmpxLib が見つかりません", StringComparison.OrdinalIgnoreCase) == true, "error should mention missing YmmpxLib");
        AssertTrue(service.SearchedPaths.Count > 0, "searched paths should be recorded");
        AssertTrue(service.SearchedPaths.Any(path => path.Contains("YmmpxLib.dll", StringComparison.OrdinalIgnoreCase)), "searched paths should include YmmpxLib.dll");
    }

    private static async Task TestPackageAndExtractWithYmmpxLibAsync(string workRoot, string ymmpxLibPath)
    {
        var root = CreateRoot(workRoot, nameof(TestPackageAndExtractWithYmmpxLibAsync));
        var assets = Path.Combine(root, "assets");
        Directory.CreateDirectory(assets);

        var imagePath = Path.Combine(assets, "image.png");
        await File.WriteAllTextAsync(imagePath, "png");
        var absoluteImagePath = Path.GetFullPath(imagePath);

        var projectPath = Path.Combine(root, "sample.ymmp");
        await File.WriteAllTextAsync(projectPath, """
        {
          "Scene": [
            { "FilePath": "assets/image.png" },
            { "FilePath": "__ABSOLUTE_IMAGE_PATH__" },
            { "Nested": { "FilePath": "file:///C:/should-not-exist.png" } }
          ]
        }
        """.Replace("__ABSOLUTE_IMAGE_PATH__", absoluteImagePath.Replace("\\", "\\\\")));

        var logger = new FileLogger(Path.Combine(root, "logs", "ymmpx.log"));
        var service = new YmmpxLibBundleService(logger, explicitAssemblyPath: ymmpxLibPath, baseDirectory: root);
        var outputPath = Path.Combine(root, "sample.ymmpx");

        var packageResult = await service.CreatePackageAsync(projectPath, outputPath, CancellationToken.None);
        AssertTrue(packageResult.Success, packageResult.ErrorMessage ?? "package creation failed");
        AssertTrue(File.Exists(outputPath), "output ymmpx should exist");
        AssertTrue(packageResult.DetectedMaterialCount >= 1, "package should detect at least one resource");
        AssertTrue(packageResult.PackagedMaterialCount >= 1, "package should include at least one resource");

        using (var archive = ZipFile.OpenRead(outputPath))
        {
            AssertTrue(archive.GetEntry("links.json") is not null, "links.json should exist for YMMResourcePackager compatibility");
            AssertTrue(archive.GetEntry("_ymmpx_project_path.txt") is not null, "_ymmpx_project_path.txt should exist");
            AssertTrue(archive.Entries.Any(x => x.FullName.StartsWith("resources/", StringComparison.OrdinalIgnoreCase)), "resources should exist");
        }

        var extractRoot = Path.Combine(root, "extract");
        var extractResult = await service.ExtractPackageAsync(outputPath, extractRoot, CancellationToken.None);

        AssertTrue(extractResult.Success, extractResult.ErrorMessage ?? "extract failed");
        AssertTrue(File.Exists(extractResult.RestoredProjectPath ?? string.Empty), "restored ymmp should exist");
        AssertTrue(Path.IsPathRooted(extractResult.RestoredProjectPath!), "restored ymmp should be absolute");
        AssertTrue(extractResult.ReplacedPathCount >= 1, "extract should replace at least one FilePath");

        var restoredJson = await File.ReadAllTextAsync(extractResult.RestoredProjectPath!);
        using var doc = JsonDocument.Parse(restoredJson);
        var restoredPaths = new List<string>();
        CollectFilePaths(doc.RootElement, restoredPaths);

        AssertTrue(restoredPaths.Count > 0, "restored project should contain FilePath values");
        AssertTrue(
            restoredPaths.Any(path => Path.IsPathRooted(path) && File.Exists(path)),
            "restored file path should point to an absolute extracted resource path");
    }

    private static string? LocateYmmpxLibDll()
    {
        var env = Environment.GetEnvironmentVariable("YMMPX_LIB_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return Path.GetFullPath(env);
        }

        var repoRoot = Directory.GetCurrentDirectory();
        var parent = Directory.GetParent(repoRoot)?.FullName;
        if (string.IsNullOrWhiteSpace(parent))
        {
            return null;
        }

        var candidates = new[]
        {
            Path.Combine(parent, "YMMPXLib", "publish", "YmmpxLib.dll"),
            Path.Combine(parent, "YMMPXLib", "YMMPXLib", "bin", "Release", "net10.0", "YmmpxLib.dll"),
            Path.Combine(parent, "YMMPXLib", "YMMPXLibPlugin", "bin", "Release", "net10.0-windows10.0.19041.0", "publish", "YmmpxLib.dll"),
            Path.Combine(parent, "YMMPXLib", "YMMPXLibPlugin", "bin", "Release", "net10.0-windows10.0.19041.0", "YmmpxLib.dll"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string CreateRoot(string workRoot, string testName)
    {
        var root = Path.Combine(workRoot, testName);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void CollectFilePaths(JsonElement element, List<string> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("FilePath") && property.Value.ValueKind == JsonValueKind.String)
                {
                    var path = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        result.Add(path);
                    }
                }
                else
                {
                    CollectFilePaths(property.Value, result);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectFilePaths(item, result);
            }
        }
    }
}
