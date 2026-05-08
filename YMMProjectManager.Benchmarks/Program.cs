using System.Diagnostics;
using System.Text;
using System.Text.Json;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Diff;
using YMMProjectManager.Infrastructure.History;
using YMMProjectManager.Presentation.Timeline;
using YMMProjectManager.Presentation.ViewModels;
using YMMProjectManager.Application.TimelineCore;

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

if (args.Any(x => string.Equals(x, "preview-validate", StringComparison.OrdinalIgnoreCase)))
{
    await RunStandalonePreviewValidationAsync();
    return;
}

await RunPerformanceBenchmarksAsync();
await RunCorrectnessBenchmarksAsync();
Console.WriteLine("Benchmark completed.");

async Task RunStandalonePreviewValidationAsync()
{
    var oldPath = Environment.GetEnvironmentVariable("YMM_STANDALONE_VALIDATION_OLD_PATH");
    var newPath = Environment.GetEnvironmentVariable("YMM_STANDALONE_VALIDATION_NEW_PATH");
    if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath))
    {
        throw new InvalidOperationException("Set YMM_STANDALONE_VALIDATION_OLD_PATH and YMM_STANDALONE_VALIDATION_NEW_PATH.");
    }

    if (!File.Exists(oldPath) || !File.Exists(newPath))
    {
        throw new FileNotFoundException($"Validation ymmp not found: old={oldPath}, new={newPath}");
    }

    var config = DiffTimelineStandaloneConfigResolver.ResolveFromEnvironment();
    var oldJson = await normalize.NormalizeFileAsync(oldPath).ConfigureAwait(false);
    var newJson = await normalize.NormalizeFileAsync(newPath).ConfigureAwait(false);
    var adapter = new YmmNormalizedJsonSnapshotAdapter(message => logger.Info(message));
    var oldSnapshot = adapter.Convert("real-old", Path.GetFileNameWithoutExtension(oldPath), oldPath, oldJson);
    var newSnapshot = adapter.Convert("real-new", Path.GetFileNameWithoutExtension(newPath), newPath, newJson);
    var cache = new InMemoryDiffTimelineSnapshotCache();
    var envelope = DiffTimelineStandalonePipeline.BuildEnvelopeFromSnapshots(
        oldSnapshot,
        newSnapshot,
        new DiffTimelineStandalonePipelineOptions(
            OptionSnapshot: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["caller"] = "YMMProjectManager.Benchmarks",
                ["entry"] = "preview-validate",
                ["snapshotSource"] = "env-real-project",
            },
            SnapshotCache: cache));
    if (!envelope.IsSuccess || envelope.Result is null)
    {
        Directory.CreateDirectory(Path.Combine(repoRoot, "diagnostics"));
        var failSummaryPath = Path.Combine(repoRoot, "diagnostics", $"preview-validation-summary-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-failed.json");
        var failPayload = new
        {
            oldPath,
            newPath,
            snapshotSource = "env-real-project",
            oldSnapshotHash = oldSnapshot.Metadata.SnapshotHash,
            newSnapshotHash = newSnapshot.Metadata.SnapshotHash,
            envelope.IsSuccess,
            envelope.FallbackReason,
            envelope.Errors,
            envelope.Warnings,
        };
        await File.WriteAllTextAsync(failSummaryPath, JsonSerializer.Serialize(failPayload, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8).ConfigureAwait(false);
        Console.WriteLine($"Preview validation failed summary captured: {failSummaryPath}");
        return;
    }

    var keys = envelope.Result.CoreResult.RowSet.Rows.Select(x => $"{x.DiffKind}|{x.Path}|{x.Field}|{x.Frame}|{x.Layer}|{x.Length}").ToList();
    var existingSummary = new DiffTimelineExistingRouteSummary(
        ItemCount: keys.Count,
        GroupCount: envelope.Result.CoreResult.Groups.Count,
        AddedCount: envelope.Result.Diagnostics.AddedCount,
        RemovedCount: envelope.Result.Diagnostics.RemovedCount,
        ChangedCount: envelope.Result.Diagnostics.ChangedCount,
        Keys: keys);
    var comparer = DiffTimelineValidationComparer.Compare(existingSummary, envelope.Result);
    var readiness = DiffTimelinePromotionReadinessEvaluator.Evaluate(comparer, envelope);
    var policy = DiffTimelineStandaloneConfigResolver.BuildPolicy(config);
    var gate = DiffTimelineStandalonePromotionGate.Evaluate(readiness, policy);
    var report = DiffTimelineStandalonePromotionGate.BuildReport(
        requestedRoute: config.StandaloneRouteEnabled ? "standalone" : "shadow-validation",
        selectedRoute: gate.Allowed ? "standalone" : "legacy-core-builder",
        readiness: readiness,
        cacheHit: envelope.CacheHit,
        diagnosticsPath: string.Empty,
        rollbackReason: envelope.FallbackReason ?? "none",
        policy: policy);
    var historyRoot = Path.Combine(repoRoot, "diagnostics");
    var historyPath = Path.Combine(historyRoot, "difftimeline-validation-run-history.json");
    var history = DiffTimelineValidationRunHistoryWriter.Load(historyPath);
    var trend = DiffTimelineValidationRegressionDetector.EvaluateTrend(history);
    var rollback = DiffTimelineStandaloneRollbackGuard.Evaluate(report, history, config, trend);
    var dashboard = DiffTimelineValidationDashboardBuilder.Build(report, trend, rollback, history);
    var selfCheck = DiffTimelineStandalonePipelineSelfCheck.Run();
    var docsPath = Path.Combine(repoRoot, "docs", "difftimeline-standalone-pipeline.md");
    var previewRunner = DiffTimelinePreviewValidationRunner.Run(
        diagnosticsDirectory: historyRoot,
        config: config,
        routeValidationReport: report,
        history: history,
        dashboard: dashboard,
        trend: trend,
        rollbackGuard: rollback,
        docsPath: docsPath,
        version: "v1-preview",
        commitHash: "562ca68");

    var summaryPath = Path.Combine(historyRoot, $"preview-validation-summary-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
    var payload = new
    {
        oldPath,
        newPath,
        snapshotSource = "env-real-project",
        oldSnapshotHash = oldSnapshot.Metadata.SnapshotHash,
        newSnapshotHash = newSnapshot.Metadata.SnapshotHash,
        rowCount = envelope.Result.Diagnostics.RowCount,
        groupCount = envelope.Result.Diagnostics.GroupCount,
        addedCount = envelope.Result.Diagnostics.AddedCount,
        removedCount = envelope.Result.Diagnostics.RemovedCount,
        changedCount = envelope.Result.Diagnostics.ChangedCount,
        cacheHit = envelope.CacheHit,
        fallbackReason = envelope.FallbackReason,
        reportBlockers = report.Blockers,
        reportWarnings = report.Warnings,
        rollbackReason = rollback.Reason,
        trend = trend.Recommendation,
        previewRunner.Succeeded,
        previewRunner.FailureReasons,
        previewRunnerWarnings = previewRunner.Warnings,
        previewRunner.ExportPackage.ExportDirectory,
        previewRunner.Manifest,
        diagnosticsMetadata = envelope.Result.Diagnostics.Metadata,
    };
    await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8).ConfigureAwait(false);
    Console.WriteLine($"Preview validation captured: {summaryPath}");
}

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
        "# YMMProjectManager Benchmark (preview13)",
        "",
        $"Date: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}",
        "",
        "| scenario | snapshotMs | normalizeMs | jsonDiffMs | ymmDiffMs | projectionMs | visibleFilterMs | zoomRecalcMs | groupingMs | visibleCount | frameCenterMs | nearestDiffSearchMs | frameJumpMs | syncStateChangeCount | pureTimelineInitializeMs | pureTimelineSetFrameMs | pureTimelineCenterFrameMs | pureTimelineFailureCount | futureYmmTimelineInitializeMs | futureYmmTimelineFailureCount | fallbackToPlaceholderCount | experimentalYmmHostInitializeMs | experimentalYmmHostSuccessCount | experimentalYmmHostFailureCount | experimentalYmmHostDisposeMs | experimentalYmmHostDisposeFailureCount | timelineReflectionProbeMs | timelineReflectionAssemblyCount | timelineReflectionTypeFoundCount | timelineReflectionFailureCount | experimentalReadyCount | matchingMsApprox |",
        "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|"
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
        _ = await snapshotService.CreateSnapshotAsync(basePath);
        swSnapshot.Stop();

        var swNormalize = Stopwatch.StartNew();
        var normalizedBase = await normalize.NormalizeFileAsync(basePath);
        var normalizedChanged = await normalize.NormalizeFileAsync(changedPath);
        swNormalize.Stop();

        var swJsonDiff = Stopwatch.StartNew();
        _ = jsonDiff.Diff(normalizedBase, normalizedChanged);
        swJsonDiff.Stop();

        var swYmmDiff = Stopwatch.StartNew();
        var ymmResult = ymmDiff.DiffWithStatistics(normalizedBase, normalizedChanged);
        swYmmDiff.Stop();

        var timelineMetrics = BenchmarkTimelineProjectionAndFiltering(ymmResult);
        var groupingMs = BenchmarkGrouping(ymmResult);
        var syncMetrics = BenchmarkSyncMetrics(ymmResult);
        var pureTimelineMetrics = await BenchmarkPureTimelineAdapterMetricsAsync();
        var futureMetrics = await BenchmarkFutureYmmAdapterMetricsAsync();
        var experimentalMetrics = await BenchmarkExperimentalYmmHostMetricsAsync();

        var matchingApprox = Math.Max(0, swYmmDiff.ElapsedMilliseconds - swJsonDiff.ElapsedMilliseconds);

        lines.Add($"| {s.Name} | {swSnapshot.ElapsedMilliseconds} | {swNormalize.ElapsedMilliseconds} | {swJsonDiff.ElapsedMilliseconds} | {swYmmDiff.ElapsedMilliseconds} | {timelineMetrics.projectionMs} | {timelineMetrics.filteringMs} | {timelineMetrics.zoomRecalcMs} | {groupingMs} | {timelineMetrics.visibleCount} | {syncMetrics.frameCenterMs} | {syncMetrics.nearestDiffSearchMs} | {syncMetrics.frameJumpMs} | {syncMetrics.syncStateChangeCount} | {pureTimelineMetrics.initializeMs} | {pureTimelineMetrics.setFrameMs} | {pureTimelineMetrics.centerFrameMs} | {pureTimelineMetrics.failureCount} | {futureMetrics.initializeMs} | {futureMetrics.failureCount} | {futureMetrics.fallbackToPlaceholderCount} | {experimentalMetrics.initializeMs} | {experimentalMetrics.successCount} | {experimentalMetrics.failureCount} | {experimentalMetrics.disposeMs} | {experimentalMetrics.disposeFailureCount} | {PureTimelineDiagnostics.TimelineReflectionProbeMs} | {PureTimelineDiagnostics.TimelineReflectionAssemblyCount} | {PureTimelineDiagnostics.TimelineReflectionTypeFoundCount} | {PureTimelineDiagnostics.TimelineReflectionFailureCount} | {PureTimelineDiagnostics.ExperimentalReadyCount} | {matchingApprox} |");
    }

    await File.WriteAllLinesAsync(logFile, lines, Encoding.UTF8);
}

async Task RunCorrectnessBenchmarksAsync()
{
    var fixturesRoot = Path.Combine(repoRoot, "YMMProjectManager.Benchmarks", "Fixtures");
    if (!Directory.Exists(fixturesRoot))
    {
        return;
    }

    var results = new List<DiffCorrectnessResult>();
    foreach (var fixtureDir in Directory.EnumerateDirectories(fixturesRoot))
    {
        var fixtureName = Path.GetFileName(fixtureDir);
        var beforePath = Path.Combine(fixtureDir, "before.ymmp");
        var afterPath = Path.Combine(fixtureDir, "after.ymmp");
        var expectedPath = Path.Combine(fixtureDir, "expected.json");
        if (!File.Exists(beforePath) || !File.Exists(afterPath) || !File.Exists(expectedPath))
        {
            continue;
        }

        var expected = JsonSerializer.Deserialize<FixtureExpectedRoot>(await File.ReadAllTextAsync(expectedPath));
        if (expected?.Expected is null)
        {
            continue;
        }

        var before = await normalize.NormalizeFileAsync(beforePath);
        var after = await normalize.NormalizeFileAsync(afterPath);
        var diffResult = ymmDiff.DiffWithStatistics(before, after);

        results.Add(new DiffCorrectnessResult
        {
            FixtureName = fixtureName,
            Passed = expected.Expected.Added == diffResult.Statistics.AddedCount
                     && expected.Expected.Removed == diffResult.Statistics.RemovedCount
                     && expected.Expected.Moved == diffResult.Statistics.MovedCount
                     && expected.Expected.Modified == diffResult.Statistics.ModifiedCount,
            ExpectedAdded = expected.Expected.Added,
            ActualAdded = diffResult.Statistics.AddedCount,
            ExpectedRemoved = expected.Expected.Removed,
            ActualRemoved = diffResult.Statistics.RemovedCount,
            ExpectedMoved = expected.Expected.Moved,
            ActualMoved = diffResult.Statistics.MovedCount,
            ExpectedModified = expected.Expected.Modified,
            ActualModified = diffResult.Statistics.ModifiedCount,
            Notes = $"idMatch={diffResult.Statistics.MatchedByInternalId}, fallbackMatch={diffResult.Statistics.MatchedByFallback}",
        });
    }

    var outputPath = Path.Combine(outputDir, $"correctness-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
}

static (long projectionMs, long filteringMs, long zoomRecalcMs, int visibleCount) BenchmarkTimelineProjectionAndFiltering(YmmProjectDiffResult diffResult)
{
    var vm = new DiffTimelineViewModel();
    var items = diffResult.Entries.Select((x, i) => vm.CreateItem($"bench-{i}", x.Kind.ToString(), x.Category, $"{x.Kind} {x.Field}", x.TimelineIndex, x.Layer, x.Frame, Math.Max(1, x.Length), x.Before, x.After)).ToList();

    var swProjection = Stopwatch.StartNew();
    vm.SetItems(items);
    swProjection.Stop();

    var swFilter = Stopwatch.StartNew();
    vm.UpdateVisibleFrameRange(500, 3000);
    vm.UpdateVisibleLayerRange(0, 12);
    var visibleCount = vm.GetVisibleItemsSnapshot().Count;
    swFilter.Stop();

    var swZoom = Stopwatch.StartNew();
    vm.ZoomIn(); vm.ZoomOut(); vm.ResetZoom();
    swZoom.Stop();

    return (swProjection.ElapsedMilliseconds, swFilter.ElapsedMilliseconds, swZoom.ElapsedMilliseconds, visibleCount);
}

static (long frameCenterMs, long nearestDiffSearchMs, long frameJumpMs, int syncStateChangeCount) BenchmarkSyncMetrics(YmmProjectDiffResult diffResult)
{
    var vm = new DiffTimelineViewModel();
    vm.SetItems(diffResult.Entries.Select((x, i) => vm.CreateItem($"s-{i}", x.Kind.ToString(), x.Category, "d", x.TimelineIndex, x.Layer, x.Frame, Math.Max(1, x.Length), x.Before, x.After)));

    var swCenter = Stopwatch.StartNew();
    vm.SetCurrentFrame(1200);
    vm.CenterCurrentFrame();
    swCenter.Stop();

    var swNearest = Stopwatch.StartNew();
    _ = vm.SelectNearestDiffToCurrentFrame();
    swNearest.Stop();

    var swJump = Stopwatch.StartNew();
    _ = vm.JumpToPreviousDiffFromCurrentFrame();
    _ = vm.JumpToNextDiffFromCurrentFrame();
    _ = vm.JumpToFirstDiff();
    _ = vm.JumpToLastDiff();
    swJump.Stop();

    var count = 0;
    vm.SetSyncMode(TimelineMode.Synced, TimelineSyncState.Detached); count++;
    vm.SetSyncMode(TimelineMode.Synced, TimelineSyncState.Synced); count++;
    vm.SetSyncMode(TimelineMode.Synced, TimelineSyncState.Manual); count++;

    return (swCenter.ElapsedMilliseconds, swNearest.ElapsedMilliseconds, swJump.ElapsedMilliseconds, count);
}

static long BenchmarkGrouping(YmmProjectDiffResult diffResult)
{
    var sw = Stopwatch.StartNew();
    _ = diffResult.Entries.GroupBy(x => x.Field).Select(x => new { x.Key, Count = x.Count() }).OrderByDescending(x => x.Count).ToList();
    sw.Stop();
    return sw.ElapsedMilliseconds;
}

static async Task<(long initializeMs, long setFrameMs, long centerFrameMs, int failureCount)> BenchmarkPureTimelineAdapterMetricsAsync()
{
    var failureCount = 0;
    var adapter = new PlaceholderPureTimelineAdapter();

    var swInit = Stopwatch.StartNew();
    var initResult = await adapter.InitializeAsync(CancellationToken.None);
    swInit.Stop();
    if (!initResult.Succeeded)
    {
        failureCount++;
    }

    var swSet = Stopwatch.StartNew();
    var setResult = await adapter.SetCurrentFrameAsync(1200, CancellationToken.None);
    swSet.Stop();
    if (!setResult.Succeeded)
    {
        failureCount++;
    }

    var swCenter = Stopwatch.StartNew();
    var centerResult = await adapter.CenterFrameAsync(1200, CancellationToken.None);
    swCenter.Stop();
    if (!centerResult.Succeeded)
    {
        failureCount++;
    }

    var disposeResult = await adapter.DisposeAsync();
    if (!disposeResult.Succeeded)
    {
        failureCount++;
    }

    return (swInit.ElapsedMilliseconds, swSet.ElapsedMilliseconds, swCenter.ElapsedMilliseconds, failureCount);
}

static async Task<(long initializeMs, int failureCount, int fallbackToPlaceholderCount)> BenchmarkFutureYmmAdapterMetricsAsync()
{
    var adapter = new FutureYmmTimelineAdapter();
    var failureCount = 0;
    var fallbackToPlaceholderCount = 0;

    var swInit = Stopwatch.StartNew();
    var init = await adapter.InitializeAsync(CancellationToken.None);
    swInit.Stop();
    if (!init.Succeeded)
    {
        failureCount++;
    }

    if (!adapter.IsAvailable)
    {
        var fallback = new PlaceholderPureTimelineAdapter();
        var fallbackInit = await fallback.InitializeAsync(CancellationToken.None);
        if (fallbackInit.Succeeded)
        {
            fallbackToPlaceholderCount++;
        }
        await fallback.DisposeAsync();
    }

    await adapter.DisposeAsync();
    return (swInit.ElapsedMilliseconds, failureCount, fallbackToPlaceholderCount);
}

static async Task<(long initializeMs, int successCount, int failureCount, long disposeMs, int disposeFailureCount)> BenchmarkExperimentalYmmHostMetricsAsync()
{
    var options = new PureTimelineExperimentalOptions
    {
        EnableExperimentalYmmTimelineHost = true,
        UseReflection = true,
        OpenIsolatedHostWindow = false,
    };

    var adapter = new FutureYmmTimelineAdapter(options);
    var swInit = Stopwatch.StartNew();
    var init = await adapter.InitializeAsync(CancellationToken.None);
    swInit.Stop();

    var successCount = init.Succeeded ? 1 : 0;
    var failureCount = init.Succeeded ? 0 : 1;

    var swDispose = Stopwatch.StartNew();
    var dispose = await adapter.DisposeAsync();
    swDispose.Stop();
    var disposeFailureCount = dispose.Succeeded ? 0 : 1;

    return (swInit.ElapsedMilliseconds, successCount, failureCount, swDispose.ElapsedMilliseconds, disposeFailureCount);
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
            items.Add(new { Type = globalIndex % 3 == 0 ? "Text" : "Media", Text = $"Item Text {globalIndex % 150}", FilePath = $"C:/media/clip_{globalIndex % 400}.wav", Frame = movedFrame, Layer = movedLayer, Length = 30 + (globalIndex % 90) });
        }
        timelines.Add(new { Index = t, Items = items });
    }
    return JsonSerializer.Serialize(new { Project = new { Name = "BenchmarkProject", Timelines = timelines } });
}

sealed record Scenario(string Name, int ItemCount, int TimelineCount);
sealed class FixtureExpectedRoot { public FixtureExpected? Expected { get; set; } }
sealed class FixtureExpected { public int Added { get; set; } public int Removed { get; set; } public int Moved { get; set; } public int Modified { get; set; } }
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
