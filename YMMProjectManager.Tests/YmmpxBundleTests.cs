using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
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
        await Program.RunNamedCaseAsync("Installer publishes only a verified download", () => TestVerifiedInstallerAsync(workRoot));
        await Program.RunNamedCaseAsync("Installer refuses unconfirmed, cancelled and failed downloads", () => TestInstallerNotPublishedAsync(workRoot));
        await Program.RunNamedCaseAsync("Installer never replaces an occupied destination", () => TestInstallerDestinationConflictAsync(workRoot));
        await Program.RunNamedCaseAsync("Installer restricts redirects to HTTPS official asset hosts", TestOfficialEndpointAsync);
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

    private static readonly byte[] VerifiedBytes = "synthetic pinned YMMPX plugin package"u8.ToArray();

    private static YmmpxLibInstallGuide CreateInstallerGuide(string root, Func<string, CancellationToken, Task<byte[]>> download) =>
        new(new FileLogger(Path.Combine(root, "installer.log")), download, VerifiedBytes.Length,
            Convert.ToHexString(SHA256.HashData(VerifiedBytes)));

    private static async Task TestVerifiedInstallerAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestVerifiedInstallerAsync));
        var destination = Path.Combine(root, "verified.ymme");
        var calls = 0;
        var guide = CreateInstallerGuide(root, (url, _) =>
        {
            AssertTrue(url == YmmpxLibInstallGuide.YmmpxLibPluginLatestDownloadUrl &&
                url.Contains("/download/v1.1.0/"), "installer must request the pinned release");
            calls++;
            return Task.FromResult(VerifiedBytes);
        });
        var result = await guide.DownloadInstallerAsync(destination, confirmed: true);
        AssertTrue(result?.Success == true && result.DownloadPath == destination && calls == 1 &&
            (await File.ReadAllBytesAsync(destination)).SequenceEqual(VerifiedBytes), "verified package was not published");
        AssertTrue(!Directory.EnumerateFiles(root, "*.download").Any(), "verified download left temporary files");
    }

    private static async Task TestInstallerNotPublishedAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestInstallerNotPublishedAsync));
        var destination = Path.Combine(root, "blocked.ymme");
        var calls = 0;
        var guide = CreateInstallerGuide(root, (_, _) =>
        {
            calls++;
            throw new HttpRequestException("simulated connection failure");
        });
        AssertTrue(await guide.DownloadInstallerAsync(destination, confirmed: false) is null && calls == 0,
            "declining reached the download");
#pragma warning disable CS0618 // Exercise the retired entry point without reaching download or launch.
        var legacy = await guide.DownloadAndLaunchInstallerAsync();
#pragma warning restore CS0618
        AssertTrue(!legacy.Success && legacy.DownloadPath is null && calls == 0,
            "the retired entry point reported success or accessed the network");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ExpectInstallerCancellationAsync(guide, destination, cancelled.Token);
        AssertTrue(calls == 0, "pre-cancellation reached the download");
        var failed = await guide.DownloadInstallerAsync(destination, confirmed: true);
        AssertTrue(failed?.Success == false && calls == 1, "download failure was not reported");

        using var inFlight = new CancellationTokenSource();
        var interrupted = CreateInstallerGuide(root, (_, _) =>
        {
            inFlight.Cancel();
            return Task.FromResult(VerifiedBytes);
        });
        await ExpectInstallerCancellationAsync(interrupted, destination, inFlight.Token);

        var changed = (byte[])VerifiedBytes.Clone();
        changed[0] ^= 1;
        foreach (var body in new[] { changed, VerifiedBytes[..^1] })
        {
            var altered = CreateInstallerGuide(root, (_, _) => Task.FromResult(body));
            var rejected = await altered.DownloadInstallerAsync(destination, confirmed: true);
            AssertTrue(rejected?.Success == false && rejected.ErrorMessage?.Contains("SHA-256") == true,
                "a wrong size or digest was accepted");
        }
        AssertTrue(!File.Exists(destination) && !Directory.EnumerateFiles(root, "*.download").Any(),
            "unconfirmed, failed or altered transfers left downloadable content");
    }

    private static async Task ExpectInstallerCancellationAsync(YmmpxLibInstallGuide guide, string destination, CancellationToken token)
    {
        try
        {
            await guide.DownloadInstallerAsync(destination, confirmed: true, token);
            throw new InvalidOperationException("installer cancellation was not observed");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static async Task TestInstallerDestinationConflictAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestInstallerDestinationConflictAsync));
        var destination = Path.Combine(root, "competing.ymme");
        const string original = "independent existing file";
        var calls = 0;
        var guide = CreateInstallerGuide(root, async (_, _) =>
        {
            calls++;
            await File.WriteAllTextAsync(destination, original);
            return VerifiedBytes;
        });

        await File.WriteAllTextAsync(destination, original);
        AssertTrue((await guide.DownloadInstallerAsync(destination, confirmed: true))?.Success == false && calls == 0,
            "existing output did not prevent the download");
        File.Delete(destination);
        AssertTrue((await guide.DownloadInstallerAsync(destination, confirmed: true))?.Success == false && calls == 1 &&
            await File.ReadAllTextAsync(destination) == original &&
            !Directory.EnumerateFiles(root, "*.download").Any(), "a competing file was overwritten or temporary output leaked");
    }

    private static Task TestOfficialEndpointAsync()
    {
        AssertTrue(YmmpxLibInstallGuide.IsOfficialAssetEndpoint(new("https://github.com/hazimeyou/YmmpxLib")) &&
            YmmpxLibInstallGuide.IsOfficialAssetEndpoint(new("https://release-assets.githubusercontent.com/file")), "official hosts were rejected");
        foreach (var url in new[] { "http://github.com/path", "https://github.com.evil.invalid/path",
            "https://github.com:444/path", "https://name@github.com/path" })
            AssertTrue(!YmmpxLibInstallGuide.IsOfficialAssetEndpoint(new(url)), "unapproved asset URL was allowed");
        return Task.CompletedTask;
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
