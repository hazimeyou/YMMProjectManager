using System.Diagnostics;
using System.Text;
using System.Text.Json;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Diff;
using YMMProjectManager.Infrastructure.History;

var repoRoot = Directory.GetCurrentDirectory();
var outputDir = Path.Combine(repoRoot, "logs", "benchmarks");
Directory.CreateDirectory(outputDir);

var logger = new FileLogger(Path.Combine(outputDir, "benchmark.log"));
var normalize = new JsonNormalizeService();
var jsonDiff = new JsonDiffService();
var ymmDiff = new YmmProjectDiffService();
var snapshotService = new ProjectSnapshotService(
    logger,
    normalize,
    new ProjectSnapshotOptions { RootDirectory = Path.Combine(Path.GetTempPath(), "YMMProjectManager-bench-history") });

await RunPerformanceBenchmarksAsync();
await RunCorrectnessBenchmarksAsync();

Console.WriteLine("Benchmark completed.");
return;

async Task RunPerformanceBenchmarksAsync()
{
    var logFile = Path.Combine(outputDir, $"benchmark-{DateTime.Now:yyyyMMdd-HHmmss}.md");

    var scenarios = new[]
    {
        new Scenario("small", 50, 2),
        new Scenario("medium", 300, 5),
        new Scenario("large", 1200, 10),
        new Scenario("extreme", 4000, 24),
    };

    var lines = new List<string>
    {
        "# YMMProjectManager Benchmark (preview5)",
        "",
        $"Date: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}",
        "",
        "| scenario | itemCount | timelineCount | snapshotMs | normalizeMs | jsonDiffMs | ymmDiffMs | matchingMsApprox | snapshotBytes | memoryBytes | idMatch | fallbackMatch |",
        "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|"
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
        var ymmResult = ymmDiff.DiffWithStatistics(normalizedBase, normalizedChanged);
        swYmmDiff.Stop();

        var afterMemory = GC.GetTotalMemory(true);
        var memoryBytes = Math.Max(0, afterMemory - beforeMemory);

        var matchingApprox = Math.Max(0, swYmmDiff.ElapsedMilliseconds - swJsonDiff.ElapsedMilliseconds);

        lines.Add($"| {s.Name} | {s.ItemCount} | {s.TimelineCount} | {swSnapshot.ElapsedMilliseconds} | {swNormalize.ElapsedMilliseconds} | {swJsonDiff.ElapsedMilliseconds} | {swYmmDiff.ElapsedMilliseconds} | {matchingApprox} | {snapshot?.OriginalFileSize ?? 0} | {memoryBytes} | {ymmResult.Statistics.MatchedByInternalId} | {ymmResult.Statistics.MatchedByFallback} |");
        lines.Add($"  - jsonDiffEntries={jsonDiffEntries.Count}, ymmDiffEntries={ymmResult.Entries.Count}");
    }

    await File.WriteAllLinesAsync(logFile, lines, Encoding.UTF8);
}

async Task RunCorrectnessBenchmarksAsync()
{
    var fixturesRoot = Path.Combine(repoRoot, "YMMProjectManager.Benchmarks", "Fixtures");
    var results = new List<DiffCorrectnessResult>();

    if (!Directory.Exists(fixturesRoot))
    {
        return;
    }

    foreach (var fixtureDir in Directory.EnumerateDirectories(fixturesRoot))
    {
        var fixtureName = Path.GetFileName(fixtureDir);
        var beforePath = Path.Combine(fixtureDir, "before.ymmp");
        var afterPath = Path.Combine(fixtureDir, "after.ymmp");
        var expectedPath = Path.Combine(fixtureDir, "expected.json");

        if (!File.Exists(beforePath) || !File.Exists(afterPath) || !File.Exists(expectedPath))
        {
            results.Add(new DiffCorrectnessResult { FixtureName = fixtureName, Passed = false, Notes = "Missing fixture file." });
            continue;
        }

        var expected = JsonSerializer.Deserialize<FixtureExpectedRoot>(await File.ReadAllTextAsync(expectedPath));
        if (expected?.Expected is null)
        {
            results.Add(new DiffCorrectnessResult { FixtureName = fixtureName, Passed = false, Notes = "Invalid expected.json." });
            continue;
        }

        var before = await normalize.NormalizeFileAsync(beforePath);
        var after = await normalize.NormalizeFileAsync(afterPath);
        var diffResult = ymmDiff.DiffWithStatistics(before, after);

        var actualAdded = diffResult.Statistics.AddedCount;
        var actualRemoved = diffResult.Statistics.RemovedCount;
        var actualMoved = diffResult.Statistics.MovedCount;
        var actualModified = diffResult.Statistics.ModifiedCount;

        var passed = expected.Expected.Added == actualAdded
                     && expected.Expected.Removed == actualRemoved
                     && expected.Expected.Moved == actualMoved
                     && expected.Expected.Modified == actualModified;

        results.Add(new DiffCorrectnessResult
        {
            FixtureName = fixtureName,
            Passed = passed,
            ExpectedAdded = expected.Expected.Added,
            ActualAdded = actualAdded,
            ExpectedRemoved = expected.Expected.Removed,
            ActualRemoved = actualRemoved,
            ExpectedMoved = expected.Expected.Moved,
            ActualMoved = actualMoved,
            ExpectedModified = expected.Expected.Modified,
            ActualModified = actualModified,
            Notes = passed
                ? $"idMatch={diffResult.Statistics.MatchedByInternalId}, fallbackMatch={diffResult.Statistics.MatchedByFallback}"
                : $"Mismatch. idMatch={diffResult.Statistics.MatchedByInternalId}, fallbackMatch={diffResult.Statistics.MatchedByFallback}",
        });
    }

    var outputPath = Path.Combine(outputDir, $"correctness-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
}

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

sealed class FixtureExpectedRoot
{
    public FixtureExpected? Expected { get; set; }
}

sealed class FixtureExpected
{
    public int Added { get; set; }
    public int Removed { get; set; }
    public int Moved { get; set; }
    public int Modified { get; set; }
}

public sealed class DiffCorrectnessResult
{
    public string FixtureName { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public int ExpectedAdded { get; set; }
    public int ActualAdded { get; set; }
    public int ExpectedRemoved { get; set; }
    public int ActualRemoved { get; set; }
    public int ExpectedMoved { get; set; }
    public int ActualMoved { get; set; }
    public int ExpectedModified { get; set; }
    public int ActualModified { get; set; }
    public string? Notes { get; set; }
}
