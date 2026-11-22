using System.IO;
using System.IO.Compression;
using System.Text.Json;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Packaging;

//CSJ,class=YmmpxBundleTests,YMMPX一括パッケージテスト
internal static class YmmpxBundleTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    public static async Task RunAsync(string workRoot)
    {
        await TestInstallGuideMessageAsync(workRoot);
        await TestLegacyFolderDetectionAsync(workRoot);
        await RunInInstallerTempAsync(workRoot, TestInstallerCancellationPreservesExistingFileAsync);
        await Program.RunNamedCaseAsync("Installer publishes only a complete download", () => RunInInstallerTempAsync(workRoot, TestInstallerDownloadAndLaunchAsync));
        await Program.RunNamedCaseAsync("Installer launch failure preserves completed download", () => RunInInstallerTempAsync(workRoot, TestInstallerLaunchFailureKeepsDownloadAsync));
        await TestResolverSearchPathsAsync(workRoot);
        var configuredLibrary = Environment.GetEnvironmentVariable("YMMPX_LIB_PATH");
        if (string.IsNullOrWhiteSpace(configuredLibrary)) await TestMissingYmmpxLibReturnsErrorAsync(workRoot);
        else
        {
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            start.ArgumentList.Add("--ymmpx-missing-fixture"); start.ArgumentList.Add(workRoot);
            start.Environment.Remove("YMMPX_LIB_PATH");
            using var child = System.Diagnostics.Process.Start(start)!;
            //CSJ,variable=timeout,タイムアウト
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await child.WaitForExitAsync(timeout.Token); }
            finally { if (!child.HasExited) child.Kill(true); }
            AssertTrue(child.ExitCode == 0, "isolated missing-library fixture failed: " + await child.StandardError.ReadToEndAsync());
        }

        //CSJ,variable=ymmpxLibPath,YMMPXライブラリパス
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
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestInstallGuideMessageAsync));
        //CSJ,variable=logger,ロガー
        var logger = new FileLogger(Path.Combine(root, "logs", "guide.log"));
        var guide = new YmmpxLibInstallGuide(logger);
        //CSJ,variable=message,メッセージ
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
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestLegacyFolderDetectionAsync));
        //CSJ,variable=logger,ロガー
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

    private static async Task TestInstallerDownloadAndLaunchAsync(string root)
    {
        //CSJ,variable=logger,ロガー
        var logger = new FileLogger(Path.Combine(root, "logs", "installer.log"));
        var launchedPaths = new List<string>();
        var guide = new YmmpxLibInstallGuide(
            logger,
            launcher: startInfo => launchedPaths.Add(startInfo.FileName),
            downloader: async (_, output, token) =>
            {
                await output.WriteAsync("dummy ymme"u8.ToArray(), token).ConfigureAwait(false);
            });

        var result = await guide.DownloadAndLaunchInstallerAsync();

        AssertTrue(result.Success, result.ErrorMessage ?? "installer flow should succeed");
        AssertTrue(!string.IsNullOrWhiteSpace(result.DownloadPath), "download path should be returned");
        AssertTrue(File.Exists(result.DownloadPath!), "downloaded ymme should exist");
        AssertTrue(launchedPaths.Count == 1, "installer should be launched once");
        AssertTrue(string.Equals(launchedPaths[0], result.DownloadPath, StringComparison.OrdinalIgnoreCase), "launched path should match downloaded ymme");
        AssertTrue(await File.ReadAllTextAsync(result.DownloadPath!) == "dummy ymme", "launched path did not contain full contents");
        AssertTrue(!Directory.EnumerateFiles(Path.GetDirectoryName(result.DownloadPath!)!, "*.download").Any(), "published installer left partial downloads");
    }

    private static async Task TestInstallerLaunchFailureKeepsDownloadAsync(string root)
    {
        string? attempted = null;
        var guide = new YmmpxLibInstallGuide(new FileLogger(Path.Combine(root, "installer.log")),
            launcher: start => { attempted = start.FileName; throw new IOException("synthetic launcher error"); },
            downloader: async (_, output, token) => await output.WriteAsync("verified bytes"u8.ToArray(), token));

        var result = await guide.DownloadAndLaunchInstallerAsync();
        AssertTrue(!result.Success && result.DownloadPath is not null && result.DownloadPath == attempted, "manual installer location missing");
        AssertTrue(await File.ReadAllTextAsync(result.DownloadPath!) == "verified bytes", "launcher failure removed completed installer");
        AssertTrue(!Directory.EnumerateFiles(Path.GetDirectoryName(result.DownloadPath!)!, "*.download").Any(), "launcher failure left partial downloads");
    }

    private static async Task RunInInstallerTempAsync(string workRoot, Func<string, Task> test)
    {
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, test.Method.Name);
        //CSJ,variable=oldTemp,旧一時
        var oldTemp = Environment.GetEnvironmentVariable("TEMP");
        var oldTmp = Environment.GetEnvironmentVariable("TMP");
        try
        {
            Environment.SetEnvironmentVariable("TEMP", root);
            Environment.SetEnvironmentVariable("TMP", root);
            AssertTrue(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) == root, "installer fixture is not isolated");
            await test(root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMP", oldTemp);
            Environment.SetEnvironmentVariable("TMP", oldTmp);
        }
    }

    private static async Task TestInstallerCancellationPreservesExistingFileAsync(string root)
    {
        var launches = 0;
        var guide = new YmmpxLibInstallGuide(new FileLogger(Path.Combine(root, "installer.log")),
            launcher: _ => launches++, downloader: (_, _, token) => { token.ThrowIfCancellationRequested(); throw new IOException("synthetic download failure"); });
        //CSJ,variable=existing,既存
        var existing = guide.GetInstallerDownloadPath();
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        await File.WriteAllTextAsync(existing, "synthetic existing installer");
        //CSJ,variable=cancelled,キャンセル済み
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await guide.DownloadAndLaunchInstallerAsync(cancelled.Token); throw new Exception("installer cancellation ignored"); }
        catch (OperationCanceledException) { }
        AssertTrue(await File.ReadAllTextAsync(existing) == "synthetic existing installer" && launches == 0, "cancelled installer modified existing file or launched");
        //CSJ,variable=failed,失敗
        var failed = await guide.DownloadAndLaunchInstallerAsync();
        AssertTrue(!failed.Success && await File.ReadAllTextAsync(existing) == "synthetic existing installer" && launches == 0, "failed download modified existing file or launched");
        //CSJ,variable=interrupted,中断済み
        using var interrupted = new CancellationTokenSource();
        var duringDownload = new YmmpxLibInstallGuide(new FileLogger(Path.Combine(root, "during.log")),
            launcher: _ => launches++, downloader: async (_, output, _) => { await output.WriteAsync("synthetic partial"u8.ToArray()); interrupted.Cancel(); });
        try { await duringDownload.DownloadAndLaunchInstallerAsync(interrupted.Token); throw new Exception("download cancellation ignored before launch"); }
        catch (OperationCanceledException) { }
        AssertTrue(await File.ReadAllTextAsync(existing) == "synthetic existing installer" && launches == 0, "cancelled download modified existing file or launched");
        AssertTrue(Directory.GetFiles(Path.GetDirectoryName(existing)!).SequenceEqual(new[] { existing }), "cancelled or failed downloads left owned partial installers");
    }

    //CSJ,method=TestResolverSearchPathsAsync,解決処理検索パス一覧をテストする（非同期）
    private static Task TestResolverSearchPathsAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestResolverSearchPathsAsync));
        //CSJ,variable=searchPaths,検索パス一覧
        var searchPaths = YmmpxLibResolver.GetSearchPaths(root, Path.Combine(root, "custom", "YmmpxLib.dll"));

        AssertTrue(searchPaths.Any(path => path.EndsWith(Path.Combine("user", "plugin", "YmmpxLibPlugin", "YmmpxLib.dll"), StringComparison.OrdinalIgnoreCase)), "search paths should include the YMM4 plugin path");
        AssertTrue(searchPaths.Contains(Path.Combine(root, "custom", "YmmpxLib.dll"), StringComparer.OrdinalIgnoreCase), "search paths should include explicit override");
        return Task.CompletedTask;
    }

    internal static async Task TestMissingYmmpxLibReturnsErrorAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestMissingYmmpxLibReturnsErrorAsync));
        //CSJ,variable=logger,ロガー
        var logger = new FileLogger(Path.Combine(root, "logs", "missing.log"));
        //CSJ,variable=service,サービス
        var service = new YmmpxLibBundleService(logger, explicitAssemblyPath: Path.Combine(root, "missing", "YmmpxLib.dll"), baseDirectory: root);

        //CSJ,variable=projectPath,プロジェクトのパス
        var projectPath = Path.Combine(root, "sample.ymmp");
        await File.WriteAllTextAsync(projectPath, """
        { "FilePath": "asset.png" }
        """);

        //CSJ,variable=cancelled,キャンセル済み
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
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestPackageAndExtractWithYmmpxLibAsync));
        //CSJ,variable=assets,素材一覧
        var assets = Path.Combine(root, "assets");
        Directory.CreateDirectory(assets);

        //CSJ,variable=imagePath,画像パス
        var imagePath = Path.Combine(assets, "image.png");
        await File.WriteAllTextAsync(imagePath, "png");
        //CSJ,variable=absoluteImagePath,絶対画像パス
        var absoluteImagePath = Path.GetFullPath(imagePath);

        //CSJ,variable=projectPath,プロジェクトのパス
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

        //CSJ,variable=logger,ロガー
        var logger = new FileLogger(Path.Combine(root, "logs", "ymmpx.log"));
        //CSJ,variable=service,サービス
        var service = new YmmpxLibBundleService(logger, explicitAssemblyPath: ymmpxLibPath, baseDirectory: root);
        //CSJ,variable=outputPath,出力パス
        var outputPath = Path.Combine(root, "sample.ymmpx");

        //CSJ,variable=packageResult,パッケージ結果
        var packageResult = await service.CreatePackageAsync(projectPath, outputPath, CancellationToken.None);
        AssertTrue(packageResult.Success, packageResult.ErrorMessage ?? "package creation failed");
        AssertTrue(File.Exists(outputPath), "output ymmpx should exist");
        AssertTrue(packageResult.DetectedMaterialCount >= 1, "package should detect at least one resource");
        AssertTrue(packageResult.PackagedMaterialCount >= 1, "package should include at least one resource");

        //CSJ,variable=archive,アーカイブ
        using (var archive = ZipFile.OpenRead(outputPath))
        {
            AssertTrue(archive.GetEntry("links.json") is not null, "links.json should exist for YMMResourcePackager compatibility");
            AssertTrue(archive.GetEntry("_ymmpx_project_path.txt") is not null, "_ymmpx_project_path.txt should exist");
            AssertTrue(archive.Entries.Any(x => x.FullName.StartsWith("resources/", StringComparison.OrdinalIgnoreCase)), "resources should exist");
        }

        //CSJ,variable=extractRoot,抽出ルート
        var extractRoot = Path.Combine(root, "extract");
        //CSJ,variable=extractResult,抽出結果
        var extractResult = await service.ExtractPackageAsync(outputPath, extractRoot, CancellationToken.None);

        AssertTrue(extractResult.Success, extractResult.ErrorMessage ?? "extract failed");
        AssertTrue(File.Exists(extractResult.RestoredProjectPath ?? string.Empty), "restored ymmp should exist");
        AssertTrue(Path.IsPathRooted(extractResult.RestoredProjectPath!), "restored ymmp should be absolute");
        AssertTrue(extractResult.ReplacedPathCount >= 1, "extract should replace at least one FilePath");

        //CSJ,variable=restoredJson,復元済みJSON
        var restoredJson = await File.ReadAllTextAsync(extractResult.RestoredProjectPath!);
        using var doc = JsonDocument.Parse(restoredJson);
        //CSJ,variable=restoredPaths,復元済みパス一覧
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

        //CSJ,variable=repoRoot,リポジトリルート
        var repoRoot = Directory.GetCurrentDirectory();
        var parent = Directory.GetParent(repoRoot)?.FullName;
        if (string.IsNullOrWhiteSpace(parent))
        {
            return null;
        }

        //CSJ,variable=candidates,候補一覧
        var candidates = new[]
        {
            Path.Combine(parent, "YMMPXLib", "publish", "YmmpxLib.dll"),
            Path.Combine(parent, "YMMPXLib", "YMMPXLib", "bin", "Release", "net10.0", "YmmpxLib.dll"),
            Path.Combine(parent, "YMMPXLib", "YMMPXLibPlugin", "bin", "Release", "net10.0-windows10.0.19041.0", "publish", "YmmpxLib.dll"),
            Path.Combine(parent, "YMMPXLib", "YMMPXLibPlugin", "bin", "Release", "net10.0-windows10.0.19041.0", "YmmpxLib.dll"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    //CSJ,method=CreateRoot,ルートを作成する
    private static string CreateRoot(string workRoot, string testName)
    {
        //CSJ,variable=root,ルート
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

    //CSJ,method=CollectFilePaths,ファイルパス一覧を収集する
    private static void CollectFilePaths(JsonElement element, List<string> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            //CSJ,variable=property,プロパティ
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("FilePath") && property.Value.ValueKind == JsonValueKind.String)
                {
                    //CSJ,variable=path,パス
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
