using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YMMProjectManager.Application.Diagnostics;
using YMMProjectManager.Application.Thumbnails;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Diagnostics;
using YMMProjectManager.Infrastructure.Generations;
using YMMProjectManager.Infrastructure.Thumbnails;
using CurrentPreviewCaptureResult = YMMProjectManager.Infrastructure.Output.CurrentPreviewCaptureResult;
using CurrentPreviewCaptureService = YMMProjectManager.Infrastructure.Output.CurrentPreviewCaptureService;
using YmmPreviewBitmapCaptureAdapter = YMMProjectManager.Infrastructure.Output.YmmPreviewBitmapCaptureAdapter;
using YmmPreviewDiscoveryResult = YMMProjectManager.Infrastructure.Output.YmmPreviewDiscoveryResult;

internal static class Program
{
    public static async Task Main()
    {
        var workRoot = Path.Combine(Path.GetTempPath(), "YMMProjectManager-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);

        try
        {
            await RunAllAsync(workRoot);
            Console.WriteLine("All generation management tests passed.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(workRoot))
                {
                    Directory.Delete(workRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static async Task RunAllAsync(string workRoot)
    {
        var filter = Environment.GetEnvironmentVariable("YMM_TEST_FILTER");
        if (!string.IsNullOrWhiteSpace(filter))
        {
            await RunFilteredAsync(workRoot, filter);
            return;
        }

        await TestZeroGenerationsAsync(workRoot);
        await TestCreateAndListAsync(workRoot);
        await TestMultipleGenerationsAsync(workRoot);
        await TestManifestCorruptionAsync(workRoot);
        await TestMetadataCorruptionAsync(workRoot);
        await TestRestoreAsync(workRoot);
        await TestDeleteAsync(workRoot);
        await TestDeletedFolderExistsAsync(workRoot);
        await TestShaMismatchAsync(workRoot);
        await TestMissingGenerationAsync(workRoot);
        await TestLockedFileRestoreFailureAsync(workRoot);
        await TestDiagnosticsAsync(workRoot);
        await TestFastThumbnailOptionsDefaultsAsync();
        await TestThumbnailFastGenerationBenchmarkOptionsDefaultsAsync();
        await TestFastThumbnailFrameSamplingAsync();
        await TestFastThumbnailResultSerializationAsync();
        await TestThumbnailFastGenerationBenchmarkResultSerializationAsync();
        await TestThumbnailFastGenerationBenchmarkSummaryReportSerializationAsync();
        await TestThumbnailFastGenerationBenchmarkComparisonCalculationAsync();
        await TestThumbnailFastGenerationBenchmarkRunnerFileGenerationAsync(workRoot);
        await TestPreviewBitmapDiagnosticsResultSerializationAsync();
        await TestPreviewBitmapMethodSignatureSerializationAsync();
        await TestPreviewBitmapCaptureResultSerializationAsync();
        await TestPreviewBitmapComparisonResultSerializationAsync();
        await TestPreviewBitmapDiagnosticsNoDispatcherAsync();
        await TestSeekAdapterReflectionFailureAsync();
        await TestSeekAdapterUsesAbsoluteTargetFrameAsync();
        await TestSeekAdapterDetectsSetterExceptionAsync();
        await TestSeekAdapterDetectsAfterFrameOutsideToleranceAsync();
        await TestSeekAdapterDiscoversTimelineFromWindowDataContextAsync();
        await TestSeekAdapterUsesCommandBindingFallbackAsync();
        await TestSeekResultSerializationAsync();
        await TestSeekAdapterCommandBindingFallbackSkipsSafelyAsync();
        await TestSeekProbeWriterAsync(workRoot);
        await TestPreviewCaptureFallbackAsync();
        await TestCurrentPreviewCaptureResultSerializationAsync();
        await TestCurrentPreviewCapturePreviewViewModelFallbackAsync(workRoot);
        await TestCurrentPreviewCaptureGetBitmapFallbackAsync(workRoot);
        await TestLegacyProjectStoreCompatibilityAsync(workRoot);
        await TestProjectEntryThumbnailCacheDirectoryNotificationAsync();
        await TestProjectGenerationStorageReplaceAsync(workRoot);
    }

    private static async Task RunFilteredAsync(string workRoot, string filter)
    {
        foreach (var testName in filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (testName)
            {
                case nameof(TestSeekAdapterReflectionFailureAsync):
                    await TestSeekAdapterReflectionFailureAsync();
                    break;
                case nameof(TestSeekAdapterUsesAbsoluteTargetFrameAsync):
                    await TestSeekAdapterUsesAbsoluteTargetFrameAsync();
                    break;
                case nameof(TestSeekAdapterDetectsSetterExceptionAsync):
                    await TestSeekAdapterDetectsSetterExceptionAsync();
                    break;
                case nameof(TestSeekAdapterDetectsAfterFrameOutsideToleranceAsync):
                    await TestSeekAdapterDetectsAfterFrameOutsideToleranceAsync();
                    break;
                case nameof(TestSeekAdapterDiscoversTimelineFromWindowDataContextAsync):
                    await TestSeekAdapterDiscoversTimelineFromWindowDataContextAsync();
                    break;
                case nameof(TestSeekAdapterUsesCommandBindingFallbackAsync):
                    await TestSeekAdapterUsesCommandBindingFallbackAsync();
                    break;
                case nameof(TestSeekResultSerializationAsync):
                    await TestSeekResultSerializationAsync();
                    break;
                case nameof(TestSeekAdapterCommandBindingFallbackSkipsSafelyAsync):
                    await TestSeekAdapterCommandBindingFallbackSkipsSafelyAsync();
                    break;
                case nameof(TestSeekProbeWriterAsync):
                    await TestSeekProbeWriterAsync(workRoot);
                    break;
                case nameof(TestThumbnailFastGenerationBenchmarkRunnerFileGenerationAsync):
                    await TestThumbnailFastGenerationBenchmarkRunnerFileGenerationAsync(workRoot);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown YMM_TEST_FILTER test: {testName}");
            }
        }
    }

    private static async Task TestZeroGenerationsAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestZeroGenerationsAsync));
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        var service = CreateService(root);

        var generations = await service.GetGenerationsAsync(projectPath);
        var diagnostics = await service.GetDiagnosticsAsync(projectPath);

        AssertEx.Equal(0, generations.Count, "Empty project should have no generations.");
        AssertEx.Equal(0, diagnostics.GenerationCount, "Diagnostics should report no valid generations.");
        AssertEx.True(diagnostics.ManifestStatus is ProjectGenerationManifestStatus.Missing, "Missing manifest should be reported for empty storage.");
    }

    private static async Task TestCreateAndListAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestCreateAndListAsync));
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        var service = CreateService(root);

        await service.CreateGenerationAsync(projectPath, "first", "memo");
        var generations = await service.GetGenerationsAsync(projectPath);

        AssertEx.Equal(1, generations.Count, "A single generation should be listed.");
        AssertEx.Equal("first", generations[0].DisplayName, "Display name should match.");
        AssertEx.True(generations[0].IsValid, "Created generation should be valid.");
    }

    private static async Task TestMultipleGenerationsAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestMultipleGenerationsAsync));
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        var service = CreateService(root);

        await service.CreateGenerationAsync(projectPath, "first", null);
        await service.CreateGenerationAsync(projectPath, "second", "memo");

        var generations = await service.GetGenerationsAsync(projectPath);
        AssertEx.Equal(2, generations.Count, "Two generations should be listed.");
    }

    private static async Task TestManifestCorruptionAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestManifestCorruptionAsync));
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        var service = CreateService(root);

        await service.CreateGenerationAsync(projectPath, "first", null);
        var manifestPath = Path.Combine(service.GetProjectDirectory(projectPath), "manifest.json");
        File.WriteAllText(manifestPath, "{ broken json");

        var generations = await service.GetGenerationsAsync(projectPath);
        var diagnostics = await service.GetDiagnosticsAsync(projectPath);

        AssertEx.Equal(0, generations.Count, "Corrupted manifest should fall back to empty list.");
        AssertEx.True(diagnostics.ManifestStatus is ProjectGenerationManifestStatus.Corrupted, "Corrupted manifest should be reported.");
    }

    private static async Task TestMetadataCorruptionAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestMetadataCorruptionAsync));
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        var service = CreateService(root);

        await service.CreateGenerationAsync(projectPath, "first", null);
        var second = await service.CreateGenerationAsync(projectPath, "second", null);
        var metadataPath = Path.Combine(service.GetProjectDirectory(projectPath), "generations", second.GenerationId, "metadata.json");
        File.Delete(metadataPath);

        var generations = await service.GetGenerationsAsync(projectPath);
        AssertEx.Equal(2, generations.Count, "Corrupted metadata should not remove the generation entry.");
        AssertEx.True(generations.Any(x => !x.IsValid), "Broken generation should be marked invalid.");
    }

    private static async Task TestRestoreAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestRestoreAsync));
        var projectPath = CreateProjectFile(root, "project.ymmp", "original");
        var service = CreateService(root);

        await service.CreateGenerationAsync(projectPath, "restore", "memo");
        await File.WriteAllTextAsync(projectPath, "changed");

        var generations = await service.GetGenerationsAsync(projectPath);
        var generationId = generations[0].GenerationId;
        var (success, errorMessage, _) = await service.RestoreGenerationAsync(projectPath, generationId, GenerationRestoreMode.RestoreToOriginalWithBackup);

        AssertEx.True(success, errorMessage ?? "Restore should succeed.");
        AssertEx.Equal("original", await File.ReadAllTextAsync(projectPath), "Project file should be restored.");

        var backupDir = Path.Combine(root, "AppData", "YMMProjectManager", "Generations", "projects", new ProjectGenerationHashService().ComputeProjectId(projectPath), "restore-backups");
        AssertEx.True(Directory.Exists(backupDir), "Backup directory should exist.");
        AssertEx.True(Directory.GetFiles(backupDir, "*.ymmp").Length > 0, "Backup file should be created.");
    }

    private static async Task TestDeleteAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestDeleteAsync));
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        var service = CreateService(root);

        var created = await service.CreateGenerationAsync(projectPath, "delete", null);
        var generationDirectory = Path.Combine(service.GetProjectDirectory(projectPath), "generations", created.GenerationId);

        var (success, errorMessage) = await service.DeleteGenerationAsync(projectPath, created.GenerationId);
        AssertEx.True(success, errorMessage ?? "Delete should succeed.");
        AssertEx.True(!Directory.Exists(generationDirectory), "Generation directory should be moved away.");
        AssertEx.Equal(0, (await service.GetGenerationsAsync(projectPath)).Count, "Deleted generation should no longer be listed.");
    }

    private static async Task TestShaMismatchAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestShaMismatchAsync));
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        var service = CreateService(root);

        var created = await service.CreateGenerationAsync(projectPath, "sha", null);
        var generationPath = Path.Combine(service.GetProjectDirectory(projectPath), "generations", created.GenerationId, "project.ymmp");
        await File.WriteAllTextAsync(generationPath, "corrupted");

        var (success, errorMessage, _) = await service.RestoreGenerationAsync(projectPath, created.GenerationId, GenerationRestoreMode.RestoreToOriginalWithBackup);
        AssertEx.True(!success, "Restore should fail for SHA mismatch.");
        AssertEx.True(!string.IsNullOrWhiteSpace(errorMessage), "Failure should include a reason.");
    }

    private static async Task TestMissingGenerationAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestMissingGenerationAsync));
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        var service = CreateService(root);

        var (success, errorMessage, _) = await service.RestoreGenerationAsync(projectPath, "missing-generation", GenerationRestoreMode.RestoreToOriginalWithBackup);
        AssertEx.True(!success, "Missing generation should fail.");
        AssertEx.True(!string.IsNullOrWhiteSpace(errorMessage), "Missing generation should provide a reason.");
    }

    private static async Task TestLockedFileRestoreFailureAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestLockedFileRestoreFailureAsync));
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        var service = CreateService(root);

        var created = await service.CreateGenerationAsync(projectPath, "locked", null);
        await File.WriteAllTextAsync(projectPath, "changed");

        using var lockStream = new FileStream(projectPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var (success, errorMessage, _) = await service.RestoreGenerationAsync(projectPath, created.GenerationId, GenerationRestoreMode.RestoreToOriginalWithBackup);

        AssertEx.True(!success, "Restore should fail when target file is locked.");
        AssertEx.True(!string.IsNullOrWhiteSpace(errorMessage), "Locked restore should provide a reason.");
    }

    private static async Task TestDeletedFolderExistsAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestDeletedFolderExistsAsync));
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        var service = CreateService(root);

        var created = await service.CreateGenerationAsync(projectPath, "delete", null);
        await service.DeleteGenerationAsync(projectPath, created.GenerationId);

        var deletedPath = Path.Combine(service.GetProjectDirectory(projectPath), "deleted");
        AssertEx.True(Directory.Exists(deletedPath), "Deleted folder should exist after deletion.");
        AssertEx.True(Directory.EnumerateDirectories(deletedPath).Any(), "Deleted folder should contain moved generations.");
    }

    private static async Task TestDiagnosticsAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestDiagnosticsAsync));
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        var service = CreateService(root);

        await service.CreateGenerationAsync(projectPath, "diag", null);
        var diagnostics = await service.GetDiagnosticsAsync(projectPath);

        AssertEx.Equal(1, diagnostics.GenerationCount, "Diagnostics should count valid generations.");
        AssertEx.True(!string.IsNullOrWhiteSpace(diagnostics.ProjectId), "ProjectId should be populated.");
        AssertEx.True(diagnostics.StorageSize > 0, "StorageSize should be populated.");
        AssertEx.True(diagnostics.LatestGeneration is not null, "LatestGeneration should be populated.");
    }

    private static Task TestFastThumbnailOptionsDefaultsAsync()
    {
        var options = new FastThumbnailGenerationOptions();

        AssertEx.True(!options.Enabled, "Fast thumbnail mode should be disabled by default.");
        AssertEx.Equal(64, options.SampleCount, "Default sample count should be 64.");
        AssertEx.Equal(50, options.SeekSettleDelayMilliseconds, "Default seek settle delay should be 50ms.");
        AssertEx.Equal(3, options.MaxRetryCount, "Default retry count should be 3.");
        AssertEx.True(!options.AllowClipboardFallback, "Clipboard fallback should be disabled by default.");
        AssertEx.True(!options.AllowScreenCaptureFallback, "Screen capture fallback should be disabled by default.");
        return Task.CompletedTask;
    }

    private static Task TestFastThumbnailFrameSamplingAsync()
    {
        var frames = FastThumbnailFrameSampler.CreateSampleFrames(64, 0, 63);

        AssertEx.Equal(64, frames.Length, "Sample count should be preserved.");
        AssertEx.Equal(0, frames[0], "First sample should be the first frame.");
        AssertEx.Equal(63, frames[^1], "Last sample should be the last frame.");
        AssertEx.True(frames.Zip(frames.Skip(1)).All(pair => pair.First <= pair.Second), "Samples should be monotonic.");
        return Task.CompletedTask;
    }

    private static Task TestFastThumbnailResultSerializationAsync()
    {
        var result = new FastThumbnailGenerationResult
        {
            Success = true,
            RequestedSampleCount = 64,
            CapturedCount = 64,
            Duration = TimeSpan.FromMilliseconds(1234),
            FallbackReason = null,
            Warnings = ["alpha", "beta"],
            Diagnostics = new ThumbnailGenerationDiagnostics
            {
                FastThumbnailEnabled = true,
                TimelineFound = true,
                PreviewViewModelFound = true,
                GetBitmapFound = true,
                SampleCount = 64,
                CapturedCount = 64,
                FailedFrameCount = 0,
                RetryCount = 0,
                AverageSeekDuration = TimeSpan.FromMilliseconds(20),
                AverageCaptureDuration = TimeSpan.FromMilliseconds(30),
                TotalDuration = TimeSpan.FromMilliseconds(1234),
                FallbackReason = null,
                Warnings = ["alpha", "beta"],
            },
        };

        var json = JsonSerializer.Serialize(result);
        var restored = JsonSerializer.Deserialize<FastThumbnailGenerationResult>(json);

        var restoredValue = restored ?? throw new InvalidOperationException("Result should deserialize.");
        var diagnostics = restoredValue.Diagnostics ?? throw new InvalidOperationException("Diagnostics should deserialize.");

        AssertEx.True(restoredValue.Success, "Success should round-trip.");
        AssertEx.Equal(64, restoredValue.RequestedSampleCount, "Requested sample count should round-trip.");
        AssertEx.Equal(64, restoredValue.CapturedCount, "Captured count should round-trip.");
        AssertEx.Equal(64, diagnostics.SampleCount, "Diagnostics sample count should round-trip.");
        return Task.CompletedTask;
    }

    private static Task TestThumbnailFastGenerationBenchmarkOptionsDefaultsAsync()
    {
        var options = new ThumbnailFastGenerationBenchmarkOptions();

        AssertEx.Equal(5, options.SampleCounts.Count, "Default benchmark sample counts should include five values.");
        AssertEx.Equal(16, options.SampleCounts[0], "First sample count should be 16.");
        AssertEx.Equal(256, options.SampleCounts[^1], "Last sample count should be 256.");
        AssertEx.Equal(4, options.SeekSettleDelayMilliseconds.Count, "Default benchmark delays should include four values.");
        AssertEx.Equal(0, options.SeekSettleDelayMilliseconds[0], "First delay should be 0ms.");
        AssertEx.Equal(100, options.SeekSettleDelayMilliseconds[^1], "Last delay should be 100ms.");
        AssertEx.Equal(3, options.MaxRetryCount, "Benchmark retry count should default to 3.");
        AssertEx.True(!options.PersistAllFrames, "Benchmark should not persist all frames by default.");
        AssertEx.True(!options.IncludeLegacyComparison, "Legacy comparison should be disabled by default.");
        AssertEx.Equal("GetBitmap(true)", options.PreferredGetBitmapCall, "Preferred call should default to GetBitmap(true).");
        return Task.CompletedTask;
    }

    private static Task TestThumbnailFastGenerationBenchmarkResultSerializationAsync()
    {
        var result = new ThumbnailFastGenerationBenchmarkResult
        {
            ProjectPath = @"C:\Temp\project.ymmp",
            GeneratedAt = new DateTimeOffset(2026, 6, 9, 21, 30, 0, TimeSpan.FromHours(9)),
            BenchmarkDirectory = @"C:\Temp\YMMProjectManager\thumbnail-fast-generation",
            BenchmarkFilePath = @"C:\Temp\YMMProjectManager\thumbnail-fast-generation\benchmark-20260609-213000.json",
            SummaryFilePath = @"C:\Temp\YMMProjectManager\thumbnail-fast-generation\benchmark-summary.json",
            OverallFailureReason = null,
            Runs =
            [
                new ThumbnailFastGenerationBenchmarkRunResult
                {
                    ProjectPath = @"C:\Temp\project.ymmp",
                    SampleCount = 64,
                    RequestedFrameCount = 64,
                    CapturedFrameCount = 64,
                    FailedFrameCount = 0,
                    RetryCount = 2,
                    TotalDurationMs = 1234.5,
                    AverageSeekDurationMs = 12.3,
                    AverageSettleDurationMs = 50.0,
                    AverageCaptureDurationMs = 20.0,
                    AverageSaveDurationMs = 2.0,
                    MinCaptureDurationMs = 10.0,
                    MaxCaptureDurationMs = 30.0,
                    MinSeekDurationMs = 5.0,
                    MaxSeekDurationMs = 15.0,
                    MinSettleDurationMs = 50.0,
                    MaxSettleDurationMs = 50.0,
                    MinSaveDurationMs = 1.0,
                    MaxSaveDurationMs = 3.0,
                    SeekMeasurementCount = 64,
                    SettleMeasurementCount = 64,
                    CaptureMeasurementCount = 64,
                    SaveMeasurementCount = 3,
                    FramesPerSecondEffective = 51.8,
                    FallbackUsed = false,
                    FallbackReason = null,
                    PreferredGetBitmapCall = "GetBitmap(true)",
                    BitmapWidth = 1920,
                    BitmapHeight = 1080,
                    BitmapPixelFormat = "Bgra32",
                    SavedFrameCount = 3,
                    GeneratedAt = new DateTimeOffset(2026, 6, 9, 21, 30, 0, TimeSpan.FromHours(9)),
                    OutputDirectory = @"C:\Temp\YMMProjectManager\thumbnail-fast-generation\capture-frames\sample-064\delay-050",
                    Warnings = ["alpha", "beta"],
                },
            ],
            Summary = new ThumbnailFastGenerationBenchmarkSummary
            {
                RunCount = 1,
                SuccessCount = 1,
                FailureCount = 0,
                TotalDurationMs = 1234,
                LegacyTotalDurationMs = null,
                SpeedupRatio = null,
                InitialTotalMemoryBytes = 1000,
                FinalTotalMemoryBytes = 2000,
                MemoryDeltaBytes = 1000,
                PostGcTotalMemoryBytes = 1500,
                PostGcMemoryDeltaBytes = 500,
            },
            LegacyComparison = new ThumbnailFastGenerationBenchmarkComparison
            {
                LegacyMeasured = false,
                SampleCount = 64,
                SeekSettleDelayMilliseconds = 50,
                LegacyTotalDurationMs = null,
                FastTotalDurationMs = 1234,
                SpeedupRatio = null,
                Reason = "Legacy comparison was not requested.",
            },
            GeneratedFiles = ["a.json", "b.png"],
        };

        var json = JsonSerializer.Serialize(result);
        var restored = JsonSerializer.Deserialize<ThumbnailFastGenerationBenchmarkResult>(json);
        var restoredValue = restored ?? throw new InvalidOperationException("Result should deserialize.");

        AssertEx.Equal(@"C:\Temp\project.ymmp", restoredValue.ProjectPath, "ProjectPath should round-trip.");
        AssertEx.Equal(1, restoredValue.Runs.Count, "Runs should round-trip.");
        AssertEx.Equal(64, restoredValue.Runs[0].SampleCount, "SampleCount should round-trip.");
        AssertEx.Equal(3, restoredValue.Runs[0].SavedFrameCount, "SavedFrameCount should round-trip.");
        AssertEx.Equal(64, restoredValue.Runs[0].CaptureMeasurementCount, "CaptureMeasurementCount should round-trip.");
        AssertEx.Equal(@"C:\Temp\YMMProjectManager\thumbnail-fast-generation\benchmark-summary.json", restoredValue.SummaryFilePath, "SummaryFilePath should round-trip.");
        AssertEx.Equal(2, restoredValue.GeneratedFiles.Count, "GeneratedFiles should round-trip.");
        return Task.CompletedTask;
    }

    private static Task TestThumbnailFastGenerationBenchmarkSummaryReportSerializationAsync()
    {
        var report = new ThumbnailFastGenerationBenchmarkSummaryReport
        {
            ProjectPath = @"C:\Temp\project.ymmp",
            GeneratedAt = new DateTimeOffset(2026, 6, 9, 21, 30, 0, TimeSpan.FromHours(9)),
            BenchmarkDirectory = @"C:\Temp\YMMProjectManager\thumbnail-fast-generation",
            SampleCounts = [16, 32, 64],
            SeekSettleDelayMilliseconds = [0, 25, 50],
            RunCount = 9,
            SuccessCount = 8,
            FailureCount = 1,
            RequestedFrameCount = 512,
            CapturedFrameCount = 510,
            FailedFrameCount = 2,
            RetryCount = 7,
            TotalDurationMs = 9876.5,
            AverageSeekDurationMs = 12.3,
            AverageSettleDurationMs = 25.0,
            AverageCaptureDurationMs = 30.0,
            AverageSaveDurationMs = 3.0,
            FramesPerSecondEffective = 51.7,
            LegacyTotalDurationMs = null,
            SpeedupRatio = null,
            OverallFailureReason = null,
            InitialTotalMemoryBytes = 1000,
            FinalTotalMemoryBytes = 1500,
            MemoryDeltaBytes = 500,
            PostGcTotalMemoryBytes = 1200,
            PostGcMemoryDeltaBytes = 200,
        };

        var json = JsonSerializer.Serialize(report);
        var restored = JsonSerializer.Deserialize<ThumbnailFastGenerationBenchmarkSummaryReport>(json);
        var restoredValue = restored ?? throw new InvalidOperationException("Result should deserialize.");

        AssertEx.Equal(9, restoredValue.RunCount, "RunCount should round-trip.");
        AssertEx.Equal(512, restoredValue.RequestedFrameCount, "RequestedFrameCount should round-trip.");
        AssertEx.Equal(51.7, restoredValue.FramesPerSecondEffective ?? 0, "FramesPerSecondEffective should round-trip.");
        return Task.CompletedTask;
    }

    private static Task TestThumbnailFastGenerationBenchmarkComparisonCalculationAsync()
    {
        var ratio = ThumbnailFastGenerationBenchmarkComparison.CalculateSpeedupRatio(1000, 250);

        AssertEx.True(ratio is not null, "Speedup ratio should be calculated.");
        AssertEx.Equal(4.0, ratio ?? 0, "Speedup ratio should use legacy/fast.");
        return Task.CompletedTask;
    }

    private static async Task TestThumbnailFastGenerationBenchmarkRunnerFileGenerationAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestThumbnailFastGenerationBenchmarkRunnerFileGenerationAsync));
        var logger = new FileLogger(Path.Combine(root, "logs", "test.log"));
        var runner = new ThumbnailFastGenerationBenchmarkRunner(
            logger,
            new FakeTimelineSeekAdapter(),
            new FakePreviewBitmapCaptureAdapter());

        var result = await runner.RunAsync(
            @"C:\Temp\project.ymmp",
            new FakeTimeline(length: 1),
            new ThumbnailFastGenerationBenchmarkOptions
            {
                SampleCounts = [1],
                SeekSettleDelayMilliseconds = [0],
                PersistAllFrames = true,
                IncludeLegacyComparison = false,
            },
            CancellationToken.None);

        AssertEx.Equal(1, result.Summary.RunCount, "Runner should execute one run.");
        AssertEx.Equal(1, result.Runs.Count, "Runner should return one run result.");
        AssertEx.True(File.Exists(result.BenchmarkFilePath ?? string.Empty), "Benchmark file should exist.");
        AssertEx.True(File.Exists(result.SummaryFilePath ?? string.Empty), "Summary file should exist.");
        AssertEx.True(result.GeneratedFiles.Any(path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)), "Runner should emit at least one PNG.");
        AssertEx.Equal(1, result.Runs[0].CapturedFrameCount, "Runner should capture one frame.");
        AssertEx.Equal(0, result.Runs[0].FailedFrameCount, "Runner should not fail the single run.");
    }

    private static Task TestPreviewBitmapDiagnosticsResultSerializationAsync()
    {
        var result = new PreviewBitmapDiagnosticsResult
        {
            DiscoverySucceeded = true,
            DiscoveryLevelReached = 4,
            WindowCount = 2,
            VisualTreeElementCount = 18,
            PreviewCandidateCount = 3,
            PreviewMethodCount = 12,
            MethodSignatureCount = 3,
            PreviewViewFound = true,
            PreviewViewModelFound = true,
            ScenePreviewViewModelFound = false,
            GetBitmapMethodFound = true,
            GetBitmapSignatureCategory = "RequiredParameters",
            GetBitmapParameterCount = 1,
            GetBitmapParameterTypes = ["System.Boolean"],
            GetBitmapInvocationCandidates = ["GetBitmap(false)", "GetBitmap(true)"],
            NextRecommendedCall = "GetBitmap(false)",
            FalseInvocationSucceeded = true,
            TrueInvocationSucceeded = true,
            FalseCaptureSucceeded = true,
            TrueCaptureSucceeded = false,
            FalseBitmapSaveSucceeded = true,
            TrueBitmapSaveSucceeded = false,
            FalseFailureKind = null,
            TrueFailureKind = "InvocationException",
            FalseHasAlpha = false,
            TrueHasAlpha = true,
            FalseWidth = 1920,
            FalseHeight = 1080,
            FalsePixelFormat = "Format24bppRgb",
            FalseFileSize = 1000,
            FalseDurationMs = 12.0,
            FalseSavedFilePath = @"C:\Temp\YMMProjectManager\PreviewDiagnostics\preview-false.png",
            TrueWidth = 1920,
            TrueHeight = 1080,
            TruePixelFormat = "Format32bppArgb",
            TrueFileSize = 2000,
            TrueDurationMs = 18.0,
            TrueSavedFilePath = @"C:\Temp\YMMProjectManager\PreviewDiagnostics\preview-true.png",
            GetBitmapInvocationSucceeded = true,
            CaptureSucceeded = true,
            PreviewViewModelTypeName = "Example.PreviewViewModel",
            GetBitmapReturnTypeName = "System.Windows.Media.Imaging.BitmapSource",
            BitmapWidth = 1920,
            BitmapHeight = 1080,
            BitmapPixelFormat = "Format24bppRgb",
            BitmapSaveSucceeded = true,
            SavedFilePath = @"C:\Temp\YMMProjectManager\PreviewDiagnostics\preview-test.png",
            FailureReason = null,
            DiagnosticWindowsPath = @"C:\Temp\YMMProjectManager\PreviewDiagnostics\diagnostic-windows.json",
            DiagnosticVisualTreePath = @"C:\Temp\YMMProjectManager\PreviewDiagnostics\diagnostic-visualtree.json",
            PreviewCandidatesPath = @"C:\Temp\YMMProjectManager\PreviewDiagnostics\preview-candidates.json",
            PreviewMethodsPath = @"C:\Temp\YMMProjectManager\PreviewDiagnostics\preview-methods.json",
            MethodSignaturesPath = @"C:\Temp\YMMProjectManager\PreviewDiagnostics\preview-method-signatures.json",
            CaptureResultPath = @"C:\Temp\YMMProjectManager\PreviewDiagnostics\capture-result.json",
            FalseCaptureResultPath = @"C:\Temp\YMMProjectManager\PreviewDiagnostics\capture-false.json",
            TrueCaptureResultPath = @"C:\Temp\YMMProjectManager\PreviewDiagnostics\capture-true.json",
            ComparisonPath = @"C:\Temp\YMMProjectManager\PreviewDiagnostics\comparison.json",
            HistoryPath = @"C:\Temp\YMMProjectManager\PreviewDiagnostics\history.json",
            Duration = TimeSpan.FromMilliseconds(456),
        };

        var json = JsonSerializer.Serialize(result);
        var restored = JsonSerializer.Deserialize<PreviewBitmapDiagnosticsResult>(json);
        var restoredValue = restored ?? throw new InvalidOperationException("Result should deserialize.");

        AssertEx.True(restoredValue.DiscoverySucceeded, "DiscoverySucceeded should round-trip.");
        AssertEx.Equal(4, restoredValue.DiscoveryLevelReached, "DiscoveryLevelReached should round-trip.");
        AssertEx.Equal(2, restoredValue.WindowCount, "WindowCount should round-trip.");
        AssertEx.True(restoredValue.PreviewViewModelFound, "PreviewViewModelFound should round-trip.");
        AssertEx.True(restoredValue.GetBitmapMethodFound, "GetBitmapMethodFound should round-trip.");
        AssertEx.True(restoredValue.GetBitmapInvocationSucceeded, "GetBitmapInvocationSucceeded should round-trip.");
        AssertEx.True(restoredValue.CaptureSucceeded, "CaptureSucceeded should round-trip.");
        AssertEx.Equal(3, restoredValue.MethodSignatureCount, "MethodSignatureCount should round-trip.");
        AssertEx.Equal("RequiredParameters", restoredValue.GetBitmapSignatureCategory, "GetBitmapSignatureCategory should round-trip.");
        AssertEx.Equal(1, restoredValue.GetBitmapParameterCount ?? 0, "GetBitmapParameterCount should round-trip.");
        AssertEx.Equal("GetBitmap(false)", restoredValue.NextRecommendedCall, "NextRecommendedCall should round-trip.");
        AssertEx.True(restoredValue.FalseInvocationSucceeded, "FalseInvocationSucceeded should round-trip.");
        AssertEx.True(restoredValue.TrueInvocationSucceeded, "TrueInvocationSucceeded should round-trip.");
        AssertEx.True(restoredValue.FalseCaptureSucceeded, "FalseCaptureSucceeded should round-trip.");
        AssertEx.True(!restoredValue.TrueCaptureSucceeded, "TrueCaptureSucceeded should round-trip.");
        AssertEx.True(restoredValue.FalseBitmapSaveSucceeded, "FalseBitmapSaveSucceeded should round-trip.");
        AssertEx.True(!restoredValue.TrueBitmapSaveSucceeded, "TrueBitmapSaveSucceeded should round-trip.");
        AssertEx.Equal("InvocationException", restoredValue.TrueFailureKind, "TrueFailureKind should round-trip.");
        AssertEx.True(restoredValue.FalseHasAlpha is not null && !restoredValue.FalseHasAlpha.Value, "FalseHasAlpha should round-trip.");
        AssertEx.True(restoredValue.TrueHasAlpha is not null && restoredValue.TrueHasAlpha.Value, "TrueHasAlpha should round-trip.");
        AssertEx.Equal(1920, restoredValue.FalseWidth ?? 0, "FalseWidth should round-trip.");
        AssertEx.Equal(1920, restoredValue.TrueWidth ?? 0, "TrueWidth should round-trip.");
        AssertEx.Equal(1920, restoredValue.BitmapWidth ?? 0, "BitmapWidth should round-trip.");
        AssertEx.Equal(1080, restoredValue.BitmapHeight ?? 0, "BitmapHeight should round-trip.");
        AssertEx.Equal("Format24bppRgb", restoredValue.BitmapPixelFormat, "BitmapPixelFormat should round-trip.");
        AssertEx.True(restoredValue.BitmapSaveSucceeded, "BitmapSaveSucceeded should round-trip.");
        return Task.CompletedTask;
    }

    private static Task TestPreviewBitmapMethodSignatureSerializationAsync()
    {
        var signature = new PreviewBitmapMethodSignatureInfo
        {
            MethodName = "GetBitmap",
            DeclaringType = "Example.PreviewViewModel",
            ReturnType = "System.Drawing.Bitmap",
            ParameterCount = 2,
            Parameters =
            [
                new PreviewBitmapMethodSignatureParameterInfo
                {
                    Name = "width",
                    Type = "System.Int32",
                    HasDefaultValue = false,
                    DefaultValue = null,
                },
                new PreviewBitmapMethodSignatureParameterInfo
                {
                    Name = "height",
                    Type = "System.Int32",
                    HasDefaultValue = false,
                    DefaultValue = null,
                },
            ],
            IsPublic = true,
            IsStatic = false,
            MatchKeyword = "GetBitmap",
            Category = "RequiredParameters",
            InvocationCandidates = ["GetBitmap(default, default)", "GetBitmap(320, 180)"],
        };

        var json = JsonSerializer.Serialize(signature);
        var restored = JsonSerializer.Deserialize<PreviewBitmapMethodSignatureInfo>(json);
        var restoredValue = restored ?? throw new InvalidOperationException("Result should deserialize.");

        AssertEx.Equal("GetBitmap", restoredValue.MethodName, "MethodName should round-trip.");
        AssertEx.Equal(2, restoredValue.ParameterCount, "ParameterCount should round-trip.");
        AssertEx.Equal("RequiredParameters", restoredValue.Category, "Category should round-trip.");
        AssertEx.Equal(2, restoredValue.InvocationCandidates.Count, "InvocationCandidates should round-trip.");
        return Task.CompletedTask;
    }

    private static Task TestPreviewBitmapCaptureResultSerializationAsync()
    {
        var result = new PreviewBitmapCaptureResult
        {
            InvocationSucceeded = true,
            ReturnType = "System.Drawing.Bitmap",
            BitmapType = "System.Drawing.Bitmap",
            Width = 1920,
            Height = 1080,
            PixelFormat = "Format32bppArgb",
            SaveSucceeded = true,
            FileSize = 123456,
            SavedFilePath = @"C:\Temp\YMMProjectManager\PreviewDiagnostics\preview-test.png",
            ExceptionType = null,
            ExceptionMessage = null,
            DurationMs = 15.5,
            CaptureSucceeded = true,
            HasAlpha = true,
            FailureKind = "None",
        };

        var json = JsonSerializer.Serialize(result);
        var restored = JsonSerializer.Deserialize<PreviewBitmapCaptureResult>(json);
        var restoredValue = restored ?? throw new InvalidOperationException("Result should deserialize.");

        AssertEx.True(restoredValue.InvocationSucceeded, "InvocationSucceeded should round-trip.");
        AssertEx.Equal(1920, restoredValue.Width ?? 0, "Width should round-trip.");
        AssertEx.Equal(1080, restoredValue.Height ?? 0, "Height should round-trip.");
        AssertEx.True(restoredValue.SaveSucceeded, "SaveSucceeded should round-trip.");
        AssertEx.True(restoredValue.CaptureSucceeded, "CaptureSucceeded should round-trip.");
        AssertEx.True(restoredValue.HasAlpha, "HasAlpha should round-trip.");
        AssertEx.Equal("None", restoredValue.FailureKind, "FailureKind should round-trip.");
        return Task.CompletedTask;
    }

    private static Task TestPreviewBitmapComparisonResultSerializationAsync()
    {
        var comparison = new PreviewBitmapComparisonResult
        {
            FalseSucceeded = true,
            TrueSucceeded = false,
            FalseInvocationSucceeded = true,
            TrueInvocationSucceeded = true,
            FalseCaptureSucceeded = true,
            TrueCaptureSucceeded = false,
            FalseWidth = 1920,
            FalseHeight = 1080,
            FalsePixelFormat = "Format24bppRgb",
            FalseHasAlpha = false,
            FalseFileSize = 1000,
            FalseDurationMs = 12,
            FalseFailureKind = "None",
            TrueWidth = 1920,
            TrueHeight = 1080,
            TruePixelFormat = "Format32bppArgb",
            TrueHasAlpha = true,
            TrueFileSize = 2000,
            TrueDurationMs = 18,
            TrueFailureKind = "InvocationException",
            PreferredCall = "GetBitmap(false)",
            Reason = "Smaller image size and identical dimensions.",
        };

        var json = JsonSerializer.Serialize(comparison);
        var restored = JsonSerializer.Deserialize<PreviewBitmapComparisonResult>(json);
        var restoredValue = restored ?? throw new InvalidOperationException("Result should deserialize.");

        AssertEx.True(restoredValue.FalseSucceeded, "FalseSucceeded should round-trip.");
        AssertEx.Equal("GetBitmap(false)", restoredValue.PreferredCall, "PreferredCall should round-trip.");
        AssertEx.Equal("Smaller image size and identical dimensions.", restoredValue.Reason, "Reason should round-trip.");
        return Task.CompletedTask;
    }

    private static async Task TestPreviewBitmapDiagnosticsNoDispatcherAsync()
    {
        var logger = new FileLogger(Path.Combine(Path.GetTempPath(), "YMMProjectManager-tests", Guid.NewGuid().ToString("N"), "logs", "test.log"));
        var service = new PreviewBitmapDiagnostics(logger);
        var result = await service.RunAsync(CancellationToken.None);

        AssertEx.True(!result.DiscoverySucceeded, "Diagnostics should fail gracefully without a WPF dispatcher.");
        AssertEx.True(!result.GetBitmapInvocationSucceeded, "Invocation should not run without a dispatcher.");
        AssertEx.True(!string.IsNullOrWhiteSpace(result.FailureReason), "Diagnostics failure should include a reason.");
    }

    private static async Task TestSeekAdapterReflectionFailureAsync()
    {
        var adapter = new YmmTimelineSeekAdapter();
        var result = await adapter.SeekAsync(new object(), 10, CancellationToken.None);

        AssertEx.True(!result.Success, "Seek should fail gracefully for unsupported objects.");
        AssertEx.True(!string.IsNullOrWhiteSpace(result.Reason), "Seek failure should include a reason.");
    }

    private static async Task TestSeekAdapterUsesAbsoluteTargetFrameAsync()
    {
        var result = await WpfTestHost.RunAsync(async () =>
        {
            var adapter = new YmmTimelineSeekAdapter();
            var timeline = new FakeTimeline(length: 100) { CurrentFrame = 10 };
            return await adapter.SeekAsync(timeline, 5, CancellationToken.None);
        });

        AssertEx.True(
            result.Success,
            $"Absolute CurrentFrame seek should succeed. success={result.Success}, before={result.BeforeFrame}, after={result.AfterFrame}, method={result.MethodUsed}, reason={result.FailureReason ?? "none"}");
        AssertEx.Equal(5, result.RequestedFrame, "RequestedFrame should be recorded.");
        AssertEx.Equal(10, result.BeforeFrame, "BeforeFrame should reflect the current frame before seek.");
        AssertEx.Equal(5, result.AfterFrame, "AfterFrame should equal the absolute target frame.");
        AssertEx.Equal(-5, result.FrameDelta, "FrameDelta should be targetFrame - beforeFrame.");
        AssertEx.Equal("CurrentFrameProperty", result.MethodUsed, "CurrentFrame setter should be the primary path.");
    }

    private static async Task TestSeekAdapterDetectsSetterExceptionAsync()
    {
        var result = await WpfTestHost.RunAsync(async () =>
        {
            var adapter = new YmmTimelineSeekAdapter();
            return await adapter.SeekAsync(new ThrowingTimeline(), 20, CancellationToken.None);
        });

        AssertEx.True(!result.Success, "Seek should fail when CurrentFrame setter throws.");
        AssertEx.True(result.ExceptionType?.Contains(nameof(InvalidOperationException), StringComparison.Ordinal) == true, "Setter exception type should be recorded.");
        AssertEx.True(!string.IsNullOrWhiteSpace(result.FailureReason), "Setter exception should be surfaced in FailureReason.");
    }

    private static async Task TestSeekAdapterDetectsAfterFrameOutsideToleranceAsync()
    {
        var result = await WpfTestHost.RunAsync(async () =>
        {
            var adapter = new YmmTimelineSeekAdapter();
            return await adapter.SeekAsync(new NonMovingTimeline(initialFrame: 7), 20, CancellationToken.None);
        });

        AssertEx.True(!result.Success, "Seek should fail when afterFrame remains outside tolerance.");
        AssertEx.Equal(7, result.BeforeFrame, "BeforeFrame should reflect the pre-seek frame.");
        AssertEx.Equal(7, result.AfterFrame, "AfterFrame should show the unchanged frame.");
        AssertEx.True(result.FailureReason?.Contains("tolerance", StringComparison.OrdinalIgnoreCase) == true, "FailureReason should explain the verification failure.");
    }

    private static async Task TestSeekAdapterDiscoversTimelineFromWindowDataContextAsync()
    {
        var result = await WpfTestHost.RunAsync(async () =>
        {
            var adapter = new YmmTimelineSeekAdapter();
            var timeline = new FakeTimeline(length: 100) { CurrentFrame = 3 };
            var window = new Window
            {
                DataContext = new TimelineContainer { Timeline = timeline },
                Width = 200,
                Height = 100,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
            };

            try
            {
                window.Show();
                return await adapter.SeekAsync(null, 12, CancellationToken.None);
            }
            finally
            {
                window.Close();
            }
        });

        AssertEx.True(result.Success, "Timeline discovery should find a DataContext timeline when the explicit argument is null.");
        AssertEx.Equal(12, result.AfterFrame, "Discovered timeline should move to the requested frame.");
    }

    private static async Task TestSeekAdapterUsesCommandBindingFallbackAsync()
    {
        var result = await WpfTestHost.RunAsync(async () =>
        {
            var adapter = new YmmTimelineSeekAdapter();
            var timeline = new CommandOnlyTimeline(initialFrame: 10);
            var command = new RoutedUICommand("Seek", "Seek", typeof(Program));
            var window = new Window
            {
                DataContext = new TimelineContainer { Timeline = timeline },
                Width = 200,
                Height = 100,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
            };

            window.CommandBindings.Add(new CommandBinding(
                command,
                (_, e) =>
                {
                    if (e.Parameter is int delta)
                    {
                        timeline.MoveBy(delta);
                    }
                },
                (_, e) =>
                {
                    e.CanExecute = e.Parameter is int;
                    e.Handled = true;
                }));

            try
            {
                window.Show();
                return await adapter.SeekAsync(null, 25, CancellationToken.None);
            }
            finally
            {
                window.Close();
            }
        });

        AssertEx.True(result.Success, "Command binding fallback should recover when CurrentFrame is read-only.");
        AssertEx.Equal(25, result.AfterFrame, "Fallback should land on the requested frame.");
        AssertEx.Equal("CommandBindingSeek", result.MethodUsed, "Seek command binding should be reported as the method used.");
    }

    private static Task TestSeekResultSerializationAsync()
    {
        var result = SeekResult.Failed(
            requestedFrame: 100,
            reason: "verification failed",
            beforeFrame: 90,
            afterFrame: 95,
            methodUsed: "CurrentFrameProperty",
            exceptionType: "System.InvalidOperationException",
            duration: TimeSpan.FromMilliseconds(12),
            tolerance: 1);

        var json = JsonSerializer.Serialize(result);
        var restored = JsonSerializer.Deserialize<SeekResult>(json);
        var restoredValue = restored ?? throw new InvalidOperationException("SeekResult should deserialize.");

        AssertEx.True(!restoredValue.Success, "Success should round-trip.");
        AssertEx.Equal(100, restoredValue.RequestedFrame, "RequestedFrame should round-trip.");
        AssertEx.Equal(90, restoredValue.BeforeFrame, "BeforeFrame should round-trip.");
        AssertEx.Equal(95, restoredValue.AfterFrame, "AfterFrame should round-trip.");
        AssertEx.Equal(10, restoredValue.FrameDelta, "FrameDelta should round-trip.");
        AssertEx.Equal("CurrentFrameProperty", restoredValue.MethodUsed, "MethodUsed should round-trip.");
        AssertEx.Equal("verification failed", restoredValue.FailureReason, "FailureReason should round-trip.");
        AssertEx.Equal("System.InvalidOperationException", restoredValue.ExceptionType, "ExceptionType should round-trip.");
        AssertEx.Equal(1, restoredValue.Tolerance, "Tolerance should round-trip.");
        return Task.CompletedTask;
    }

    private static async Task TestSeekProbeWriterAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestSeekProbeWriterAsync));
        var outputDirectory = Path.Combine(root, "seek-probe");
        string path = string.Empty;

        await WpfTestHost.RunAsync(async () =>
        {
            var adapter = new YmmTimelineSeekAdapter();
            var timeline = new FakeTimeline(length: 120) { CurrentFrame = 4 };
            path = await adapter.WriteSeekProbeAsync(timeline, 20, outputDirectory, CancellationToken.None);
            return true;
        });

        AssertEx.True(File.Exists(path), "Seek probe JSON should be created.");
        var json = await File.ReadAllTextAsync(path);
        var result = JsonSerializer.Deserialize<SeekResult>(json) ?? throw new InvalidOperationException("Seek probe JSON should deserialize.");
        AssertEx.True(result.Success, "Seek probe should record a successful seek.");
        AssertEx.Equal(20, result.RequestedFrame, "Seek probe should record RequestedFrame.");
        AssertEx.Equal(4, result.BeforeFrame, "Seek probe should record BeforeFrame.");
        AssertEx.Equal(20, result.AfterFrame, "Seek probe should record AfterFrame.");
    }

    private static async Task TestSeekAdapterCommandBindingFallbackSkipsSafelyAsync()
    {
        var result = await WpfTestHost.RunAsync(async () =>
        {
            var adapter = new YmmTimelineSeekAdapter();
            var timeline = new CommandOnlyTimeline(initialFrame: 2);
            var window = new Window
            {
                DataContext = new TimelineContainer { Timeline = timeline },
                Width = 200,
                Height = 100,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
            };

            try
            {
                window.Show();
                return await adapter.SeekAsync(null, 8, CancellationToken.None);
            }
            finally
            {
                window.Close();
            }
        });

        AssertEx.True(!result.Success, "Fallback should fail gracefully when no matching command binding exists.");
        AssertEx.True(result.FailureReason?.Contains("command binding fallback", StringComparison.OrdinalIgnoreCase) == true, "FailureReason should explain that fallback was skipped safely.");
    }

    private static async Task TestPreviewCaptureFallbackAsync()
    {
        var adapter = new YmmPreviewBitmapCaptureAdapter();
        var result = await adapter.TryCaptureAsync(CancellationToken.None);

        AssertEx.True(!result.Success, "Preview capture should fail gracefully when no preview VM is available.");
        AssertEx.True(!string.IsNullOrWhiteSpace(result.FailureReason), "Preview capture failure should include a reason.");
    }

    private static Task TestCurrentPreviewCaptureResultSerializationAsync()
    {
        var result = new CurrentPreviewCaptureResult
        {
            Timestamp = new DateTimeOffset(2026, 6, 9, 23, 30, 0, TimeSpan.FromHours(9)),
            Success = true,
            FailureReason = null,
            WindowCount = 17,
            VisualTreeElementCount = 5223,
            PreviewViewFound = true,
            PreviewViewModelFound = true,
            GetBitmapMethodFound = true,
            GetBitmapParameterTypes = ["System.Boolean"],
            NextRecommendedCall = "GetBitmap(true)",
            InvocationSucceeded = true,
            CaptureSucceeded = true,
            BitmapWidth = 1920,
            BitmapHeight = 1080,
            BitmapPixelFormat = "Bgr32",
            SavedPath = @"C:\Temp\current-preview.png",
            DiagnosticsPath = @"C:\Temp\current-preview.json",
            DurationMs = 12.5,
        };

        var json = JsonSerializer.Serialize(result);
        var restored = JsonSerializer.Deserialize<CurrentPreviewCaptureResult>(json);
        var restoredValue = restored ?? throw new InvalidOperationException("Result should deserialize.");

        AssertEx.True(restoredValue.Success, "Success should round-trip.");
        AssertEx.Equal(17, restoredValue.WindowCount, "WindowCount should round-trip.");
        AssertEx.True(restoredValue.InvocationSucceeded, "InvocationSucceeded should round-trip.");
        AssertEx.Equal("GetBitmap(true)", restoredValue.NextRecommendedCall, "NextRecommendedCall should round-trip.");
        AssertEx.Equal(1920, restoredValue.BitmapWidth ?? 0, "BitmapWidth should round-trip.");
        AssertEx.Equal(@"C:\Temp\current-preview.png", restoredValue.SavedPath, "SavedPath should round-trip.");
        return Task.CompletedTask;
    }

    private static async Task TestCurrentPreviewCapturePreviewViewModelFallbackAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestCurrentPreviewCapturePreviewViewModelFallbackAsync));
        var logger = new FileLogger(Path.Combine(root, "logs", "test.log"));
        var service = new CurrentPreviewCaptureService(
            logger,
            captureAdapter: new FakeFailingPreviewBitmapCaptureAdapter("PreviewViewModel not found"),
            outputDirectory: Path.Combine(root, "output"),
            discoverAsync: _ => Task.FromResult(new YmmPreviewDiscoveryResult
            {
                DiscoverySucceeded = false,
                FailureReason = "PreviewViewModel not found",
                FailureStage = "PreviewViewModelNotFound",
                WindowCount = 3,
                VisualTreeElementCount = 20,
                PreviewViewFound = true,
                PreviewViewModelFound = false,
                GetBitmapMethodFound = false,
                GetBitmapParameterTypes = [],
                NextRecommendedCall = "GetBitmap(true)",
            }));

        var result = await service.CaptureAsync(CancellationToken.None);

        AssertEx.True(!result.Success, "Current preview capture should fail gracefully when preview VM is missing.");
        AssertEx.Equal("PreviewViewModel not found", result.FailureReason, "Missing preview VM should be reported.");
        AssertEx.True(File.Exists(result.DiagnosticsPath ?? string.Empty), "Diagnostics JSON should still be emitted.");
    }

    private static async Task TestCurrentPreviewCaptureGetBitmapFallbackAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestCurrentPreviewCaptureGetBitmapFallbackAsync));
        var logger = new FileLogger(Path.Combine(root, "logs", "test.log"));
        var service = new CurrentPreviewCaptureService(
            logger,
            captureAdapter: new FakeFailingPreviewBitmapCaptureAdapter("GetBitmap not found"),
            outputDirectory: Path.Combine(root, "output"),
            discoverAsync: _ => Task.FromResult(new YmmPreviewDiscoveryResult
            {
                DiscoverySucceeded = false,
                FailureReason = "GetBitmap not found",
                FailureStage = "GetBitmapNotFound",
                WindowCount = 4,
                VisualTreeElementCount = 25,
                PreviewViewFound = true,
                PreviewViewModelFound = true,
                GetBitmapMethodFound = false,
                GetBitmapParameterTypes = ["System.Boolean"],
                NextRecommendedCall = "GetBitmap(true)",
            }));

        var result = await service.CaptureAsync(CancellationToken.None);

        AssertEx.True(!result.Success, "Current preview capture should fail gracefully when GetBitmap is missing.");
        AssertEx.Equal("GetBitmap not found", result.FailureReason, "Missing GetBitmap should be reported.");
        AssertEx.True(File.Exists(result.DiagnosticsPath ?? string.Empty), "Diagnostics JSON should still be emitted.");
    }

    private static async Task TestLegacyProjectStoreCompatibilityAsync(string workRoot)
    {
        var runtimeRoot = Path.Combine(CreateRoot(workRoot, nameof(TestLegacyProjectStoreCompatibilityAsync)), "YMM4");
        var dataDir = Path.Combine(runtimeRoot, "user", "plugin", "YMMProjectManager", "data");
        Directory.CreateDirectory(dataDir);

        var json = """
        {
          "Projects": [
            {
              "FullPath": "C:\\Temp\\legacy.ymmp",
              "DisplayName": "legacy project",
              "Pinned": true,
              "LastAccess": "2026-06-08T00:00:00+09:00"
            }
          ]
        }
        """;

        var original = Environment.GetEnvironmentVariable("YMM4DirPath");
        try
        {
            Environment.SetEnvironmentVariable("YMM4DirPath", runtimeRoot);
            var jsonPath = Path.Combine(dataDir, "projects.json");
            await File.WriteAllTextAsync(jsonPath, json);

            var logger = new FileLogger(Path.Combine(runtimeRoot, "logs", "test.log"));
            var repository = new JsonProjectRepository(logger);
            var store = await repository.LoadAsync();

            AssertEx.Equal(1, store.Projects.Count, "Legacy project JSON should load.");
            AssertEx.Equal("legacy project", store.Projects[0].DisplayName, "Legacy display name should round-trip.");
            AssertEx.True(store.Projects[0].LinkedYmmpFiles.Count >= 1, "Legacy project should be normalized with a linked main file.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("YMM4DirPath", original);
        }
    }

    private static Task TestProjectEntryThumbnailCacheDirectoryNotificationAsync()
    {
        var entry = new ProjectEntry();
        var notifications = new List<string>();

        entry.PropertyChanged += (_, e) => notifications.Add(e.PropertyName ?? string.Empty);

        entry.ThumbnailCacheDirectory = @"C:\Temp\cache-a";
        entry.ThumbnailCacheDirectory = @"C:\Temp\cache-b";

        AssertEx.Equal(2, notifications.Count, "Thumbnail cache directory changes should notify twice.");
        AssertEx.Equal(nameof(ProjectEntry.ThumbnailCacheDirectory), notifications[0], "Thumbnail cache directory should notify its own property name.");
        return Task.CompletedTask;
    }

    private static async Task TestProjectGenerationStorageReplaceAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestProjectGenerationStorageReplaceAsync));
        var storage = new ProjectGenerationStorage(Path.Combine(root, "store"));
        var targetDirectory = Path.Combine(root, "target");
        Directory.CreateDirectory(targetDirectory);

        var targetPath = Path.Combine(targetDirectory, "project.ymmp");
        await File.WriteAllTextAsync(targetPath, "old");

        var sourceTempPath = Path.Combine(root, "source.tmp");
        await File.WriteAllTextAsync(sourceTempPath, "new");

        await storage.ReplaceFileAtomicallyAsync(sourceTempPath, targetPath, null);

        AssertEx.Equal("new", await File.ReadAllTextAsync(targetPath), "Atomic replace should overwrite the target content.");
        AssertEx.True(!File.Exists(sourceTempPath), "Atomic replace should consume the source temp file.");
    }

    private static ProjectGenerationService CreateService(string root)
    {
        var logger = new FileLogger(Path.Combine(root, "logs", "test.log"));
        var storageRoot = Path.Combine(root, "AppData", "YMMProjectManager", "Generations");
        return new ProjectGenerationService(logger, storageRoot);
    }

    private static string CreateRoot(string workRoot, string testName)
    {
        var root = Path.Combine(workRoot, testName);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateProjectFile(string root, string fileName, string content)
    {
        var path = Path.Combine(root, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class TimelineContainer
    {
        public object? Timeline { get; init; }
    }

    private sealed class FakeTimeline
    {
        public FakeTimeline(int length)
        {
            Length = length;
        }

        public int Length { get; }

        public int CurrentFrame { get; set; }
    }

    private sealed class ThrowingTimeline
    {
        public int CurrentFrame
        {
            get => 10;
            set => throw new InvalidOperationException("setter boom");
        }
    }

    private sealed class NonMovingTimeline
    {
        private int currentFrame;

        public NonMovingTimeline(int initialFrame)
        {
            currentFrame = initialFrame;
        }

        public int CurrentFrame
        {
            get => currentFrame;
            set
            {
            }
        }
    }

    private sealed class CommandOnlyTimeline
    {
        private int currentFrame;

        public CommandOnlyTimeline(int initialFrame)
        {
            currentFrame = initialFrame;
        }

        public int CurrentFrame => currentFrame;

        public void MoveBy(int delta)
        {
            currentFrame += delta;
        }
    }

    private sealed class FakeTimelineSeekAdapter : ITimelineSeekAdapter
    {
        public Task<SeekResult> SeekAsync(object? timeline, int targetFrame, CancellationToken cancellationToken)
        {
            if (timeline is FakeTimeline fakeTimeline)
            {
                fakeTimeline.CurrentFrame = targetFrame;
            }

            return Task.FromResult(SeekResult.Succeeded(targetFrame, targetFrame));
        }
    }

    private sealed class FakePreviewBitmapCaptureAdapter : IPreviewBitmapCaptureAdapter
    {
        public Task<PreviewCaptureResult> TryCaptureAsync(CancellationToken cancellationToken)
        {
            var pixels = new byte[2 * 2 * 4];
            pixels[0] = 0x20;
            pixels[1] = 0x40;
            pixels[2] = 0x80;
            pixels[3] = 0xFF;
            pixels[4] = 0x10;
            pixels[5] = 0x20;
            pixels[6] = 0x30;
            pixels[7] = 0xFF;
            pixels[8] = 0x60;
            pixels[9] = 0x70;
            pixels[10] = 0x80;
            pixels[11] = 0xFF;
            pixels[12] = 0xA0;
            pixels[13] = 0xB0;
            pixels[14] = 0xC0;
            pixels[15] = 0xFF;

            var bitmap = BitmapSource.Create(
                2,
                2,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                2 * 4);

            if (!bitmap.IsFrozen && bitmap.CanFreeze)
            {
                bitmap.Freeze();
            }

            return Task.FromResult(PreviewCaptureResult.Succeeded(bitmap, "System.Windows.Media.Imaging.BitmapSource"));
        }
    }

    private sealed class FakeFailingPreviewBitmapCaptureAdapter : IPreviewBitmapCaptureAdapter
    {
        private readonly string reason;

        public FakeFailingPreviewBitmapCaptureAdapter(string reason)
        {
            this.reason = reason;
        }

        public Task<PreviewCaptureResult> TryCaptureAsync(CancellationToken cancellationToken)
            => Task.FromResult(PreviewCaptureResult.Failed(reason));
    }

    private static class WpfTestHost
    {
        private static readonly object SyncRoot = new();
        private static Thread? thread;
        private static Dispatcher? dispatcher;
        private static Application? application;

        public static Task<T> RunAsync<T>(Func<Task<T>> action)
        {
            EnsureStarted();

            var taskCompletionSource = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatcher!.BeginInvoke(new Action(async () =>
            {
                try
                {
                    taskCompletionSource.SetResult(await action());
                }
                catch (Exception ex)
                {
                    taskCompletionSource.SetException(ex);
                }
            }), DispatcherPriority.Send);

            return taskCompletionSource.Task;
        }

        public static void Shutdown()
        {
            Dispatcher? dispatcherToShutdown;
            Thread? threadToJoin;

            lock (SyncRoot)
            {
                dispatcherToShutdown = dispatcher;
                threadToJoin = thread;
                dispatcher = null;
                thread = null;
                application = null;
            }

            if (dispatcherToShutdown is not null)
            {
                dispatcherToShutdown.Invoke(() => Application.Current?.Shutdown());
                dispatcherToShutdown.BeginInvokeShutdown(DispatcherPriority.Send);
            }

            if (threadToJoin is not null && threadToJoin.IsAlive)
            {
                threadToJoin.Join(TimeSpan.FromSeconds(5));
            }
        }

        private static void EnsureStarted()
        {
            lock (SyncRoot)
            {
                if (dispatcher is not null)
                {
                    return;
                }

                var started = new ManualResetEventSlim(false);
                Exception? startupException = null;

                thread = new Thread(() =>
                {
                    try
                    {
                        application = new Application
                        {
                            ShutdownMode = ShutdownMode.OnExplicitShutdown,
                        };
                        dispatcher = Dispatcher.CurrentDispatcher;
                        started.Set();
                        Dispatcher.Run();
                    }
                    catch (Exception ex)
                    {
                        startupException = ex;
                        started.Set();
                    }
                })
                {
                    IsBackground = true,
                    Name = "YMMProjectManager.Tests.WpfTestHost",
                };

                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                started.Wait();

                if (startupException is not null)
                {
                    throw startupException;
                }
            }
        }
    }

    private static class AssertEx
    {
        public static void True(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException($"{message} Expected={expected}, Actual={actual}");
            }
        }
    }
}
