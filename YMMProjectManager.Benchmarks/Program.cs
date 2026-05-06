using System.Diagnostics;
using System.Text;
using System.Text.Json;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Diff;
using YMMProjectManager.Infrastructure.History;

var outputDir = Path.Combine(AppContext.BaseDirectory, "logs", "benchmarks");
Directory.CreateDirectory(outputDir);
var logFile = Path.Combine(outputDir, $"benchmark-{DateTime.Now:yyyyMMdd-HHmmss}.md");

var logger = new FileLogger(Path.Combine(outputDir, "benchmark.log"));
var normalize = new JsonNormalizeService();
var jsonDiff = new JsonDiffService();
var ymmDiff = new YmmProjectDiffService();
var snapshotService = new ProjectSnapshotService(
    logger,
    normalize,
    new ProjectSnapshotOptions { RootDirectory = Path.Combine(Path.GetTempPath(), "YMMProjectManager-bench-history") });

var scenarios = new[]
{
    new Scenario("small", 50, 2),
    new Scenario("medium", 300, 5),
    new Scenario("large", 1200, 10),
    new Scenario("extreme", 4000, 24),
};

var lines = new List<string>
{
    "# YMMProjectManager Benchmark (preview4)",
    "",
    $"Date: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}",
    "",
    "| scenario | itemCount | timelineCount | snapshotMs | normalizeMs | jsonDiffMs | ymmDiffMs | matchingMsApprox | snapshotBytes | memoryBytes |",
    "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|"
};

foreach (var s in scenarios)
{
    var baseJson = GenerateProjectJson(s.ItemCount, s.TimelineCount, moved: false);
    var changedJson = GenerateProjectJson(s.ItemCount, s.TimelineCount, moved: true);

    var tmpDir = Path.Combine(Path.GetTempPath(), "YMMProjectManager-bench", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    var basePath = Path.Combine(tmpDir, "base.ymmp");
    var changedPath = Path.Combine(tmpDir, "changed.ymmp");

    await File.WriteAllTextAsync(basePath, baseJson, Encoding.UTF8);
    await File.WriteAllTextAsync(changedPath, changedJson, Encoding.UTF8);

    var swSnapshot = Stopwatch.StartNew();
    var snapshot = await snapshotService.CreateSnapshotAsync(basePath);
    swSnapshot.Stop();

    var swNormalize = Stopwatch.StartNew();
    var normalizedBase = await normalize.NormalizeFileAsync(basePath);
    var normalizedChanged = await normalize.NormalizeFileAsync(changedPath);
    swNormalize.Stop();

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var beforeMemory = GC.GetTotalMemory(true);

    var swJsonDiff = Stopwatch.StartNew();
    var jsonDiffEntries = jsonDiff.Diff(normalizedBase, normalizedChanged);
    swJsonDiff.Stop();

    var swYmmDiff = Stopwatch.StartNew();
    var ymmDiffEntries = ymmDiff.Diff(normalizedBase, normalizedChanged);
    swYmmDiff.Stop();

    var afterMemory = GC.GetTotalMemory(true);
    var memoryBytes = Math.Max(0, afterMemory - beforeMemory);

    // Rough estimate: YmmDiff total minus JsonDiff total (matching+semantic layers).
    var matchingApprox = Math.Max(0, swYmmDiff.ElapsedMilliseconds - swJsonDiff.ElapsedMilliseconds);

    lines.Add($"| {s.Name} | {s.ItemCount} | {s.TimelineCount} | {swSnapshot.ElapsedMilliseconds} | {swNormalize.ElapsedMilliseconds} | {swJsonDiff.ElapsedMilliseconds} | {swYmmDiff.ElapsedMilliseconds} | {matchingApprox} | {snapshot?.OriginalFileSize ?? 0} | {memoryBytes} |");
    lines.Add($"  - jsonDiffEntries={jsonDiffEntries.Count}, ymmDiffEntries={ymmDiffEntries.Count}");
}

await File.WriteAllLinesAsync(logFile, lines, Encoding.UTF8);
Console.WriteLine($"Benchmark report: {logFile}");

return;

static string GenerateProjectJson(int itemCount, int timelineCount, bool moved)
{
    var timelines = new List<object>();
    var perTimeline = Math.Max(1, itemCount / Math.Max(1, timelineCount));

    for (var t = 0; t < timelineCount; t++)
    {
        var items = new List<object>();
        for (var i = 0; i < perTimeline; i++)
        {
            var globalIndex = (t * perTimeline) + i;
            var frame = globalIndex * 3;
            var layer = (globalIndex % 12) + 1;
            var movedFrame = moved && globalIndex % 20 == 0 ? frame + 7 : frame;
            var movedLayer = moved && globalIndex % 35 == 0 ? Math.Min(24, layer + 1) : layer;

            items.Add(new
            {
                Type = globalIndex % 3 == 0 ? "Text" : "Media",
                Text = $"Item Text {globalIndex % 150}",
                FilePath = $"C:/media/clip_{globalIndex % 400}.wav",
                Frame = movedFrame,
                Layer = movedLayer,
                Length = 30 + (globalIndex % 90),
            });
        }

        timelines.Add(new { Index = t, Items = items });
    }

    var model = new
    {
        Project = new
        {
            Name = "BenchmarkProject",
            Timelines = timelines,
        }
    };

    return JsonSerializer.Serialize(model);
}

sealed record Scenario(string Name, int ItemCount, int TimelineCount);
