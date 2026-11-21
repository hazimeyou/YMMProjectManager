using System.IO;
using System.Text.Json;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Packaging;

internal static class YmmpxReleasedFormatTests
{
    public static async Task RunAsync(string workRoot)
    {
        var fixtureRoot = Environment.GetEnvironmentVariable("YMM_TEST_YMMPX_RELEASED_FIXTURES");
        foreach (var version in new[] { "v0.2.1", "v0.3.0", "v1.0.1" })
        {
            var fixture = fixtureRoot is null ? null : Path.Combine(fixtureRoot, version, "sample.ymmpx");
            if (!File.Exists(fixture))
            {
                Program.ReportConditionalSkip($"Released YMMPX {version} fixture unavailable");
                continue;
            }

            await Program.RunNamedCaseAsync($"R23 reads package created by released YMMPX {version}", async () =>
            {
                var root = Directory.CreateDirectory(Path.Combine(workRoot, "released-" + version)).FullName;
                var service = new YmmpxLibBundleService(new FileLogger(Path.Combine(root, "test.log")), baseDirectory: root);
                var destination = Path.Combine(root, "restored");
                var extracted = await service.ExtractPackageAsync(fixture!, destination, default);
                if (!extracted.Success || extracted.TransferEvidenceVerified || extracted.ExtractedResourceCount != 1 ||
                    extracted.RestoredProjectPath is not { } projectPath)
                    throw new InvalidOperationException(extracted.ErrorMessage ?? "Existing format was not restored as a legacy package.");
                using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(projectPath));
                var media = json.RootElement.GetProperty("Items")[0].GetProperty("FilePath").GetString();
                if (media is null || !Path.GetFullPath(media).StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    !json.RootElement.GetProperty("Unknown").GetProperty("Keep").GetBoolean() ||
                    await File.ReadAllTextAsync(media) != "R23 legacy synthetic resource")
                    throw new InvalidOperationException("Restored asset bytes or the unknown project field changed.");
            });
        }
    }
}
