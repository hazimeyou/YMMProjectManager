using System.IO;
using System.Text.Json;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Packaging;

//CSJ,class=YmmpxReleasedFormatTests,公開済みYMMPX形式のテスト
internal static class YmmpxReleasedFormatTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    public static async Task RunAsync(string workRoot)
    {
        //CSJ,variable=fixtureRoot,テスト用データルート
        var fixtureRoot = Environment.GetEnvironmentVariable("YMM_TEST_YMMPX_RELEASED_FIXTURES");
        //CSJ,variable=version,バージョン
        foreach (var version in new[] { "v0.2.1", "v0.3.0", "v1.0.1" })
        {
            //CSJ,variable=fixture,テスト用データ
            var fixture = fixtureRoot is null ? null : Path.Combine(fixtureRoot, version, "sample.ymmpx");
            if (!File.Exists(fixture))
            {
                Program.ReportConditionalSkip($"Released YMMPX {version} fixture unavailable");
                continue;
            }

            await Program.RunNamedCaseAsync($"R23 reads package created by released YMMPX {version}", async () =>
            {
                //CSJ,variable=root,ルート
                var root = Directory.CreateDirectory(Path.Combine(workRoot, "released-" + version)).FullName;
                //CSJ,variable=service,サービス
                var service = new YmmpxLibBundleService(new FileLogger(Path.Combine(root, "test.log")), baseDirectory: root);
                //CSJ,variable=destination,格納先
                var destination = Path.Combine(root, "restored");
                var extracted = await service.ExtractPackageAsync(fixture!, destination, default);
                if (!extracted.Success || extracted.TransferEvidenceVerified || extracted.ExtractedResourceCount != 1 ||
                    extracted.RestoredProjectPath is not { } projectPath)
                    throw new InvalidOperationException(extracted.ErrorMessage ?? "Existing format was not restored as a legacy package.");
                using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(projectPath));
                //CSJ,variable=media,メディア
                var media = json.RootElement.GetProperty("Items")[0].GetProperty("FilePath").GetString();
                if (media is null || !Path.GetFullPath(media).StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    !json.RootElement.GetProperty("Unknown").GetProperty("Keep").GetBoolean() ||
                    await File.ReadAllTextAsync(media) != "R23 legacy synthetic resource")
                    throw new InvalidOperationException("Restored asset bytes or the unknown project field changed.");
            });
        }
    }
}
