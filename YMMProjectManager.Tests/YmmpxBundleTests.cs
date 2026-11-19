using System.IO;
using System.IO.Compression;
using System.Text.Json;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Packaging;

internal static class YmmpxBundleTests
{
    public static async Task RunAsync(string workRoot)
    {
        await TestInstallGuideMessageAsync(workRoot);
        await TestLegacyFolderDetectionAsync(workRoot);
        await TestInstallerCancellationPreservesExistingFileAsync(workRoot);
        await TestInstallerDownloadAndLaunchAsync(workRoot);
        await TestResolverSearchPathsAsync(workRoot);
        var configuredLibrary = Environment.GetEnvironmentVariable("YMMPX_LIB_PATH");
        if (string.IsNullOrWhiteSpace(configuredLibrary)) await TestMissingYmmpxLibReturnsErrorAsync(workRoot);
        else
        {
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            start.ArgumentList.Add("--ymmpx-missing-fixture"); start.ArgumentList.Add(workRoot);
            start.Environment.Remove("YMMPX_LIB_PATH");
            using var child = System.Diagnostics.Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await child.WaitForExitAsync(timeout.Token); }
            finally { if (!child.HasExited) child.Kill(true); }
            AssertTrue(child.ExitCode == 0, "isolated missing-library fixture failed: " + await child.StandardError.ReadToEndAsync());
        }

        var ymmpxLibPath = LocateYmmpxLibDll();
        if (!string.IsNullOrWhiteSpace(ymmpxLibPath))
        {
            await TestPackageAndExtractWithYmmpxLibAsync(workRoot, ymmpxLibPath);
        }
        else
        {
            Program.ReportConditionalSkip("real YmmpxLib unavailable");
        }
    }

    private static Task TestInstallGuideMessageAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestInstallGuideMessageAsync));
        var logger = new FileLogger(Path.Combine(root, "logs", "guide.log"));
        var guide = new YmmpxLibInstallGuide(logger);
        var message = guide.BuildMissingPluginMessage(
            [Path.Combine(root, "user", "plugin", "YmmpxLibPlugin", "YmmpxLib.dll")],
            []);

        AssertTrue(message.Contains("YmmpxLibPlugin が見つかりません"), "guide should mention missing plugin");
        AssertTrue(message.Contains("user"), "guide should include install path");
        AssertTrue(message.Contains(YmmpxLibInstallGuide.YmmpxLibPluginLatestDownloadUrl), "guide should include download url");
        AssertTrue(message.Contains("再起動"), "guide should mention YMM restart");
        return Task.CompletedTask;
    }

    private static Task TestLegacyFolderDetectionAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestLegacyFolderDetectionAsync));
        var logger = new FileLogger(Path.Combine(root, "logs", "legacy.log"));
        var guide = new YmmpxLibInstallGuide(logger);
        var legacyA = Path.Combine(root, "user", "plugin", "YMMProjectManager", "YmmpxLib");
        var legacyB = Path.Combine(root, "user", "plugin", "YmmpxLib");
        Directory.CreateDirectory(legacyA);
        Directory.CreateDirectory(legacyB);

        var detected = guide.FindLegacyFolders(root);

        AssertTrue(detected.Contains(Path.GetFullPath(legacyA), StringComparer.OrdinalIgnoreCase), "legacy YMMProjectManager/YmmpxLib should be detected");
        AssertTrue(detected.Contains(Path.GetFullPath(legacyB), StringComparer.OrdinalIgnoreCase), "legacy YmmpxLib should be detected");
        return Task.CompletedTask;
    }

    private static async Task TestInstallerDownloadAndLaunchAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestInstallerDownloadAndLaunchAsync));
        var logger = new FileLogger(Path.Combine(root, "logs", "installer.log"));
        var launchedPaths = new List<string>();
        var guide = new YmmpxLibInstallGuide(
            logger,
            launcher: startInfo => launchedPaths.Add(startInfo.FileName),
            downloader: async (_, path, _) =>
            {
                await File.WriteAllTextAsync(path, "dummy ymme").ConfigureAwait(false);
            });

        var result = await guide.DownloadAndLaunchInstallerAsync();

        AssertTrue(result.Success, result.ErrorMessage ?? "installer flow should succeed");
        AssertTrue(!string.IsNullOrWhiteSpace(result.DownloadPath), "download path should be returned");
        AssertTrue(File.Exists(result.DownloadPath!), "downloaded ymme should exist");
        AssertTrue(launchedPaths.Count == 1, "installer should be launched once");
        AssertTrue(string.Equals(launchedPaths[0], result.DownloadPath, StringComparison.OrdinalIgnoreCase), "launched path should match downloaded ymme");
    }

    private static async Task TestInstallerCancellationPreservesExistingFileAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestInstallerCancellationPreservesExistingFileAsync));
        var oldTemp = Environment.GetEnvironmentVariable("TEMP");
        var oldTmp = Environment.GetEnvironmentVariable("TMP");
        try
        {
            Environment.SetEnvironmentVariable("TEMP", root);
            Environment.SetEnvironmentVariable("TMP", root);
            AssertTrue(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) == Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), "installer fixture is not isolated");
            var launches = 0;
            var guide = new YmmpxLibInstallGuide(new FileLogger(Path.Combine(root, "installer.log")),
                launcher: _ => launches++, downloader: (_, _, token) => { token.ThrowIfCancellationRequested(); throw new IOException("synthetic download failure"); });
            var existing = guide.GetInstallerDownloadPath();
            Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
            await File.WriteAllTextAsync(existing, "synthetic existing installer");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await guide.DownloadAndLaunchInstallerAsync(cancelled.Token); throw new Exception("installer cancellation ignored"); }
            catch (OperationCanceledException) { }
            AssertTrue(await File.ReadAllTextAsync(existing) == "synthetic existing installer" && launches == 0, "cancelled installer modified existing file or launched");
            var failed = await guide.DownloadAndLaunchInstallerAsync();
            AssertTrue(!failed.Success && await File.ReadAllTextAsync(existing) == "synthetic existing installer" && launches == 0, "failed download modified existing file or launched");
            using var interrupted = new CancellationTokenSource();
            var duringDownload = new YmmpxLibInstallGuide(new FileLogger(Path.Combine(root, "during.log")),
                launcher: _ => launches++, downloader: async (_, path, _) => { await File.WriteAllTextAsync(path, "synthetic partial"); interrupted.Cancel(); });
            try { await duringDownload.DownloadAndLaunchInstallerAsync(interrupted.Token); throw new Exception("download cancellation ignored before launch"); }
            catch (OperationCanceledException) { }
            AssertTrue(await File.ReadAllTextAsync(existing) == "synthetic existing installer" && launches == 0, "cancelled download modified existing file or launched");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMP", oldTemp);
            Environment.SetEnvironmentVariable("TMP", oldTmp);
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

    internal static async Task TestMissingYmmpxLibReturnsErrorAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestMissingYmmpxLibReturnsErrorAsync));
        var logger = new FileLogger(Path.Combine(root, "logs", "missing.log"));
        var service = new YmmpxLibBundleService(logger, explicitAssemblyPath: Path.Combine(root, "missing", "YmmpxLib.dll"), baseDirectory: root);

        var projectPath = Path.Combine(root, "sample.ymmp");
        await File.WriteAllTextAsync(projectPath, """
        { "FilePath": "asset.png" }
        """);

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await service.CreatePackageAsync(projectPath, Path.Combine(root, "cancelled.ymmpx"), cancelled.Token); throw new Exception("missing-library package cancellation was ignored"); }
            catch (OperationCanceledException) { }
            try { await service.ExtractPackageAsync(Path.Combine(root, "missing.ymmpx"), Path.Combine(root, "extract"), cancelled.Token); throw new Exception("missing-library extraction cancellation was ignored"); }
            catch (OperationCanceledException) { }
            AssertTrue(!File.Exists(Path.Combine(root, "cancelled.ymmpx")) && !Directory.Exists(Path.Combine(root, "extract")), "cancelled operation created output");
            AssertTrue(service.SearchedPaths.Count == 0, "cancelled operation resolved a library");
        }

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

        if (!extractResult.Success)
        {
            AssertTrue(extractResult.ErrorMessage == "展開先の保護を検証中のため、ymmpx の展開は利用できません。", "unexpected extraction failure");
            AssertTrue(!Directory.Exists(extractRoot), "disabled extraction created output");
            Program.ReportConditionalSkip("product Ymmpx extraction success remains disabled");
            await RealYmmpxR5Tests.RunAsync(workRoot);
            return;
        }

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
