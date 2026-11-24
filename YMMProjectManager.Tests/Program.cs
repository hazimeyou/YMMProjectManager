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
using YMMProjectManager.Tests;
using CurrentPreviewCaptureResult = YMMProjectManager.Infrastructure.Output.CurrentPreviewCaptureResult;
using CurrentPreviewCaptureService = YMMProjectManager.Infrastructure.Output.CurrentPreviewCaptureService;
using YmmPreviewBitmapCaptureAdapter = YMMProjectManager.Infrastructure.Output.YmmPreviewBitmapCaptureAdapter;
using YmmPreviewDiscoveryResult = YMMProjectManager.Infrastructure.Output.YmmPreviewDiscoveryResult;

//CSJ,class=Program,テスト実行プログラム
internal static class Program
{
    private static int groupsSelected;
    private static int groupsPassed;
    private static int groupsWithSkips;
    private static int groupsFailed;
    private static int conditionalSkipped;
    private static int namedCasesPassed;
    private static int namedCasesFailed;

    //CSJ,method=RunNamedCaseAsync,名前付きテストケースを実行する（非同期）
    internal static async Task RunNamedCaseAsync(string name, Func<Task> run)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try { await run(); namedCasesPassed++; Console.WriteLine($"PASS case: {name} ({timer.Elapsed})"); }
        catch { namedCasesFailed++; Console.Error.WriteLine($"FAIL case: {name} ({timer.Elapsed})"); throw; }
    }

    internal static void ReportConditionalSkip(string name)
    {
        conditionalSkipped++;
        Console.WriteLine($"SKIP conditional case: {name}");
    }

    public static async Task Main(string[] args)
    {
        if (args.Length == 2 && args[0] == AssetPoolTests.ChildArgument)
        {
            await AssetPoolTests.RunChildAsync(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] is ProjectListWpfFixtureProcess.ChildArgument or ProjectListWpfFixtureProcess.StalledChildArgument)
        {
            await ProjectListWpfFixtureProcess.RunChildAsync(args[1], stalled: args[0] == ProjectListWpfFixtureProcess.StalledChildArgument);
            return;
        }
        if (args.Length == 2 && args[0] == GitPullRecoveryTests.ChildArgument)
        {
            GitPullRecoveryTests.RunInterruptedChild(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == GitPullRecoveryTests.AppliedChildArgument)
        {
            await GitPullRecoveryTests.RunInterruptedAfterMergeAsync(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == GitNativeInterruptionTests.RestartArgument)
        {
            await GitNativeInterruptionTests.VerifyRestartAsync(args[1]);
            return;
        }
        if (args.Length == 3 && args[0] == "--list-publication-crash")
        {
            await ProjectListPublicationTests.CrashAsync(args[1], args[2]);
            return;
        }
        if (args.Length == 2 && args[0] == "--r16-relink-publication-child")
        {
            await AssetManifestTests.InterruptPreparedOutputAsync(args[1]);
            return;
        }
        if (args.Length == 3 && args[0] == "--project-list-worker")
        {
            await ProjectListConcurrencyTests.RunWorkerAsync(args[1], args[2]);
            return;
        }
        if (args.Length == 2 && args[0] == "--ymmpx-missing-fixture")
        {
            //CSJ,variable=allowed,許可済み
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "YMMProjectManager-tests")) + Path.DirectorySeparatorChar;
            //CSJ,variable=fixtureRoot,テスト用データルート
            var fixtureRoot = Path.GetFullPath(args[1]);
            if (!fixtureRoot.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture outside isolated root.");
            await YmmpxBundleTests.TestMissingYmmpxLibReturnsErrorAsync(fixtureRoot);
            return;
        }
        var timer = System.Diagnostics.Stopwatch.StartNew();
        //CSJ,variable=configuration,構成
        var configuration = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .OfType<System.Reflection.AssemblyConfigurationAttribute>().SingleOrDefault()?.Configuration ?? "unknown";
        //CSJ,variable=library,ライブラリ
        var library = Environment.GetEnvironmentVariable("YMMPX_LIB_PATH");
        Console.WriteLine($"Configuration={configuration}; explicitly configured YmmpxLib present={File.Exists(library)}");
        //CSJ,variable=workRoot,作業ルート
        var workRoot = Path.Combine(Path.GetTempPath(), "YMMProjectManager-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        var previousGitGlobal = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        var previousGitSystem = Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", Path.Combine(workRoot, "empty-git-config"));
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");

        try
        {
            await RunTestsAsync(workRoot);
            if (groupsFailed > 0) Environment.ExitCode = 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", previousGitGlobal);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", previousGitSystem);
            var groupsExecuted = groupsPassed + groupsWithSkips + groupsFailed;
            Console.WriteLine($"Test groups executed: total={groupsExecuted}, passed={groupsPassed}, withSkips={groupsWithSkips}, failed={groupsFailed}, selectedNotRun={groupsSelected - groupsExecuted}, duration={timer.Elapsed}");
            Console.WriteLine($"Explicit conditional skips: {conditionalSkipped}. Counts are top-level groups; other nested cases and skips are not counted.");
            Console.WriteLine($"Individually instrumented cases: passed={namedCasesPassed}, failed={namedCasesFailed}; legacy nested checks are outside this count.");
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

    private sealed record TestGroup(string Name, Func<Task> Run, bool Default = true, string? Alias = null);

    private static IReadOnlyList<TestGroup> GetTestGroups(string workRoot) =>
    [
        new(nameof(PreviewComparisonTests), PreviewComparisonTests.RunAsync),
        new(nameof(SceneComparisonHandoffTests), SceneComparisonHandoffTests.RunAsync),
        new(nameof(SceneRenderCoordinatorTests), SceneRenderCoordinatorTests.RunAsync),
        new(nameof(GitPublicationTests), () => GitPublicationTests.RunAsync(workRoot)),
        new(nameof(AssetPoolTests), () => AssetPoolTests.RunAsync(workRoot)),
        new(nameof(DerivedRelinkAssociationTests), () => DerivedRelinkAssociationTests.RunAsync(workRoot)),
        new(nameof(ExternalRelinkRecoveryTests), () => ExternalRelinkRecoveryTests.RunAsync(workRoot)),
        new(nameof(ProjectFileCleanupTests), ProjectFileCleanupTests.RunAsync),
        new(nameof(StrictRelinkWorkflowTests), () => StrictRelinkWorkflowTests.RunAsync(workRoot)),
        new(nameof(TestZeroGenerationsAsync), () => TestZeroGenerationsAsync(workRoot)),
        new(nameof(TestCreateAndListAsync), () => TestCreateAndListAsync(workRoot)),
        new(nameof(TestMultipleGenerationsAsync), () => TestMultipleGenerationsAsync(workRoot)),
        new(nameof(TestManifestCorruptionAsync), () => TestManifestCorruptionAsync(workRoot)),
        new(nameof(GenerationManifestProtectionTests), () => GenerationManifestProtectionTests.RunAsync(workRoot)),
        new(nameof(ManifestWriteHardeningTests), () => ManifestWriteHardeningTests.RunAsync(workRoot)),
        new(nameof(GenerationIdCollisionTests), () => GenerationIdCollisionTests.RunAsync(workRoot)),
        new(nameof(RelinkIntegrityTests), () => RelinkIntegrityTests.RunAsync(workRoot)),
        new(nameof(TestMetadataCorruptionAsync), () => TestMetadataCorruptionAsync(workRoot)),
        new(nameof(TestRestoreAsync), () => TestRestoreAsync(workRoot)),
        new(nameof(TestDeleteAsync), () => TestDeleteAsync(workRoot)),
        new(nameof(TestDeletedFolderExistsAsync), () => TestDeletedFolderExistsAsync(workRoot)),
        new(nameof(TestShaMismatchAsync), () => TestShaMismatchAsync(workRoot)),
        new(nameof(TestMissingGenerationAsync), () => TestMissingGenerationAsync(workRoot)),
        new(nameof(TestLockedFileRestoreFailureAsync), () => TestLockedFileRestoreFailureAsync(workRoot)),
        new(nameof(TestDiagnosticsAsync), () => TestDiagnosticsAsync(workRoot)),
        new(nameof(TestFastThumbnailOptionsDefaultsAsync), () => TestFastThumbnailOptionsDefaultsAsync()),
        new(nameof(TestThumbnailFastGenerationBenchmarkOptionsDefaultsAsync), () => TestThumbnailFastGenerationBenchmarkOptionsDefaultsAsync()),
        new(nameof(TestFastThumbnailFrameSamplingAsync), () => TestFastThumbnailFrameSamplingAsync()),
        new(nameof(TestFastThumbnailResultSerializationAsync), () => TestFastThumbnailResultSerializationAsync()),
        new(nameof(TestThumbnailFastGenerationBenchmarkResultSerializationAsync), () => TestThumbnailFastGenerationBenchmarkResultSerializationAsync()),
        new(nameof(TestThumbnailFastGenerationBenchmarkSummaryReportSerializationAsync), () => TestThumbnailFastGenerationBenchmarkSummaryReportSerializationAsync()),
        new(nameof(TestThumbnailFastGenerationBenchmarkComparisonCalculationAsync), () => TestThumbnailFastGenerationBenchmarkComparisonCalculationAsync()),
        new(nameof(TestThumbnailFastGenerationBenchmarkRunnerFileGenerationAsync), () => TestThumbnailFastGenerationBenchmarkRunnerFileGenerationAsync(workRoot)),
        new(nameof(TestPreviewBitmapDiagnosticsResultSerializationAsync), () => TestPreviewBitmapDiagnosticsResultSerializationAsync()),
        new(nameof(TestPreviewBitmapMethodSignatureSerializationAsync), () => TestPreviewBitmapMethodSignatureSerializationAsync()),
        new(nameof(TestPreviewBitmapCaptureResultSerializationAsync), () => TestPreviewBitmapCaptureResultSerializationAsync()),
        new(nameof(TestPreviewBitmapComparisonResultSerializationAsync), () => TestPreviewBitmapComparisonResultSerializationAsync()),
        new(nameof(TestPreviewBitmapDiagnosticsNoDispatcherAsync), () => TestPreviewBitmapDiagnosticsNoDispatcherAsync()),
        new(nameof(TestSeekAdapterReflectionFailureAsync), () => TestSeekAdapterReflectionFailureAsync()),
        new(nameof(TestSeekAdapterUsesAbsoluteTargetFrameAsync), () => TestSeekAdapterUsesAbsoluteTargetFrameAsync()),
        new(nameof(TestSeekAdapterDetectsSetterExceptionAsync), () => TestSeekAdapterDetectsSetterExceptionAsync()),
        new(nameof(TestSeekAdapterDetectsAfterFrameOutsideToleranceAsync), () => TestSeekAdapterDetectsAfterFrameOutsideToleranceAsync()),
        new(nameof(TestSeekAdapterDiscoversTimelineFromWindowDataContextAsync), () => TestSeekAdapterDiscoversTimelineFromWindowDataContextAsync()),
        new(nameof(TestSeekAdapterUsesCommandBindingFallbackAsync), () => TestSeekAdapterUsesCommandBindingFallbackAsync()),
        new(nameof(TestSeekResultSerializationAsync), () => TestSeekResultSerializationAsync()),
        new(nameof(TestSeekAdapterCommandBindingFallbackSkipsSafelyAsync), () => TestSeekAdapterCommandBindingFallbackSkipsSafelyAsync()),
        new(nameof(TestSeekProbeWriterAsync), () => TestSeekProbeWriterAsync(workRoot)),
        new(nameof(TestPreviewCaptureFallbackAsync), () => TestPreviewCaptureFallbackAsync()),
        new(nameof(TestCurrentPreviewCaptureResultSerializationAsync), () => TestCurrentPreviewCaptureResultSerializationAsync()),
        new(nameof(TestCurrentPreviewCapturePreviewViewModelFallbackAsync), () => TestCurrentPreviewCapturePreviewViewModelFallbackAsync(workRoot)),
        new(nameof(TestCurrentPreviewCaptureGetBitmapFallbackAsync), () => TestCurrentPreviewCaptureGetBitmapFallbackAsync(workRoot)),
        new(nameof(TestLegacyProjectStoreCompatibilityAsync), () => TestLegacyProjectStoreCompatibilityAsync(workRoot)),
        new(nameof(ProjectListSaveTests), () => ProjectListSaveTests.RunAsync(workRoot)),
        new(nameof(ProjectListConcurrencyTests), () => ProjectListConcurrencyTests.RunAsync(workRoot)),
        new(nameof(ProjectListDraftTests), () => ProjectListDraftTests.RunAsync(workRoot)),
        new(nameof(ProjectListPublicationTests), () => ProjectListPublicationTests.RunAsync(workRoot)),
        new(nameof(ProjectListIdentityRecoveryTests), () =>
            RunNamedCaseAsync("Explicit duplicate project ID repair without modifying the source", () => ProjectListIdentityRecoveryTests.RunAsync(workRoot))),
        new(nameof(ProjectListRecoveryFilesTests), () =>
            RunNamedCaseAsync("Three-way recovery verifies the selected file and holds its lease", () => ProjectListRecoveryFilesTests.RunAsync(workRoot))),
        new(nameof(RelinkAssociationTests), () =>
            RunNamedCaseAsync("Relink SaveAs, revision and WPF association retry", () => RelinkAssociationTests.RunAsync(workRoot))),
        new(nameof(ProjectListRecoveryTests), () => ProjectListWpfFixtureProcess.RunAsync(workRoot)),
        new("ProjectListWpfFixtureProcess.VerifyTimeoutAsync", () =>
            RunNamedCaseAsync("WPF watchdog stops only the owned child", () => ProjectListWpfFixtureProcess.VerifyTimeoutAsync(workRoot))),
        new(nameof(TestProjectEntryThumbnailCacheDirectoryNotificationAsync), () => TestProjectEntryThumbnailCacheDirectoryNotificationAsync()),
        new(nameof(TestProjectGenerationStorageReplaceAsync), () => TestProjectGenerationStorageReplaceAsync(workRoot)),
        new(nameof(YmmpxBundleTests), () => YmmpxBundleTests.RunAsync(workRoot)),
        new(nameof(YmmpxFailureTests), () => YmmpxFailureTests.RunAsync(workRoot)),
        new(nameof(YmmpxArchiveValidationTests), () => YmmpxArchiveValidationTests.RunAsync(workRoot)),
        new(nameof(YmmpxTransferTests), () => YmmpxTransferTests.RunAsync(workRoot)),
        new(nameof(YmmpxReleasedFormatTests), () => YmmpxReleasedFormatTests.RunAsync(workRoot)),
        new(nameof(GitRepositoryTests), () => GitRepositoryTests.RunAsync(workRoot)),
        new(nameof(GitHubBrowserLoginTests), () => GitHubBrowserLoginTests.RunAsync(workRoot)),
        new(nameof(GitPullRecoveryTests), () => GitPullRecoveryTests.RunAsync(workRoot)),
        new(nameof(GitRemoteTests), () => GitRemoteTests.RunAsync(workRoot)),
        new(nameof(GitNativeInterruptionTests), () => GitNativeInterruptionTests.RunAsync(workRoot)),
        new(nameof(OfflineControlsTests), () => OfflineControlsTests.RunAsync(workRoot)),
        new(nameof(AssetInventoryTests), () => AssetInventoryTests.RunAsync(workRoot)),
        new(nameof(AssetManifestTests), () => AssetManifestTests.RunAsync(workRoot)),
        new(nameof(GitHttpsAcceptanceTests), () => GitHttpsAcceptanceTests.RunAsync(workRoot), Default: false),
        new(nameof(RealYmmpxR5Tests), () => RealYmmpxR5Tests.RunAsync(workRoot), Default: false, Alias: "RealYmmpxR5"),
        new(nameof(TestRunnerSelectionAsync), () => TestRunnerSelectionAsync(workRoot)),
    ];

    private static IReadOnlyList<TestGroup> SelectTestGroups(IReadOnlyList<TestGroup> groups, string? filter)
    {
        if (string.IsNullOrEmpty(filter))
            return groups.Where(group => group.Default).ToArray();

        var selected = new List<TestGroup>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var group = groups.SingleOrDefault(candidate => name == candidate.Name || name == candidate.Alias ||
                name == candidate.Name + ".RunAsync")
                ?? throw new ArgumentException($"Unknown YMM_TEST_FILTER group: {name}");
            if (!names.Add(group.Name))
                throw new ArgumentException($"Duplicate YMM_TEST_FILTER group: {group.Name}");
            selected.Add(group);
        }
        if (selected.Count == 0)
            throw new ArgumentException("YMM_TEST_FILTER selected no test groups.");
        return selected;
    }

    private static async Task RunTestsAsync(string workRoot)
    {
        var groups = GetTestGroups(workRoot);
        var filter = Environment.GetEnvironmentVariable("YMM_TEST_FILTER");
        IReadOnlyList<TestGroup> selected;
        try
        {
            selected = SelectTestGroups(groups, filter);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"FAIL test selection: {ex.Message} None of the {groups.Count} registered groups ran.");
            Environment.ExitCode = 1;
            return;
        }

        groupsSelected = selected.Count;
        Console.WriteLine($"Test groups selected: {groupsSelected} of {groups.Count}. Not selected: {groups.Count - groupsSelected}.");
        if (string.IsNullOrEmpty(filter))
            foreach (var group in groups.Where(group => !group.Default))
                Console.WriteLine($"NOT RUN group: {group.Name} (requires explicit selection)");

        foreach (var group in selected)
            await RunGroupAsync(group);
    }

    //CSJ,method=RunGroupAsync,グループを実行する（非同期）
    private static async Task RunGroupAsync(TestGroup group)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var skipsBefore = conditionalSkipped;
        try
        {
            await group.Run();
            var skips = conditionalSkipped - skipsBefore;
            if (skips == 0)
            {
                groupsPassed++;
                Console.WriteLine($"PASS group: {group.Name} ({timer.Elapsed})");
            }
            else
            {
                groupsWithSkips++;
                Console.WriteLine($"INCOMPLETE group: {group.Name} ({skips} explicit skips, {timer.Elapsed})");
            }
        }
        catch (Exception ex)
        {
            groupsFailed++;
            Console.Error.WriteLine($"FAIL group: {group.Name} ({timer.Elapsed}). Subsequent checks within this group may not have run.\n{ex}");
        }
    }

    private static Task TestRunnerSelectionAsync(string workRoot) =>
        RunNamedCaseAsync("Test runner resolves all names before executing selected groups", () =>
        {
            var groups = GetTestGroups(workRoot);
            var defaults = SelectTestGroups(groups, null).Select(group => group.Name).ToArray();
            if (!defaults.Contains(nameof(GitPullRecoveryTests)) || !defaults.Contains(nameof(GitRemoteTests)) ||
                defaults.Contains(nameof(GitHttpsAcceptanceTests)) || defaults.Contains(nameof(RealYmmpxR5Tests)))
                throw new InvalidOperationException("Unexpected default test groups.");
            var selected = SelectTestGroups(groups, "GitRemoteTests.RunAsync, RealYmmpxR5, GitHttpsAcceptanceTests")
                .Select(group => group.Name);
            if (!selected.SequenceEqual([nameof(GitRemoteTests), nameof(RealYmmpxR5Tests), nameof(GitHttpsAcceptanceTests)]))
                throw new InvalidOperationException("Selected test groups do not match the requested names.");
            foreach (var invalidFilter in new[] { " , , ", "GitRemoteTests,unknown", "GitRemoteTests,GitRemoteTests.RunAsync" })
            {
                try { SelectTestGroups(groups, invalidFilter); }
                catch (ArgumentException) { continue; }
                throw new InvalidOperationException($"The runner accepted an invalid filter: {invalidFilter}");
            }
            return Task.CompletedTask;
        });

    private static async Task TestZeroGenerationsAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestZeroGenerationsAsync));
        //CSJ,variable=projectPath,プロジェクトのパス
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        //CSJ,variable=service,サービス
        var service = CreateService(root);

        //CSJ,variable=generations,世代
        var generations = await service.GetGenerationsAsync(projectPath);
        //CSJ,variable=diagnostics,診断情報
        var diagnostics = await service.GetDiagnosticsAsync(projectPath);

        AssertEx.Equal(0, generations.Count, "Empty project should have no generations.");
        AssertEx.Equal(0, diagnostics.GenerationCount, "Diagnostics should report no valid generations.");
        AssertEx.True(diagnostics.ManifestStatus is ProjectGenerationManifestStatus.Missing, "Missing manifest should be reported for empty storage.");
    }

    private static async Task TestCreateAndListAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestCreateAndListAsync));
        //CSJ,variable=projectPath,プロジェクトのパス
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        //CSJ,variable=service,サービス
        var service = CreateService(root);

        await service.CreateGenerationAsync(projectPath, "first", "memo");
        //CSJ,variable=generations,世代
        var generations = await service.GetGenerationsAsync(projectPath);

        AssertEx.Equal(1, generations.Count, "A single generation should be listed.");
        AssertEx.Equal("first", generations[0].DisplayName, "Display name should match.");
        AssertEx.True(generations[0].IsValid, "Created generation should be valid.");
    }

    //CSJ,method=TestMultipleGenerationsAsync,複数世代をテストする（非同期）
    private static async Task TestMultipleGenerationsAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestMultipleGenerationsAsync));
        //CSJ,variable=projectPath,プロジェクトのパス
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        //CSJ,variable=service,サービス
        var service = CreateService(root);

        await service.CreateGenerationAsync(projectPath, "first", null);
        await service.CreateGenerationAsync(projectPath, "second", "memo");

        //CSJ,variable=generations,世代
        var generations = await service.GetGenerationsAsync(projectPath);
        AssertEx.Equal(2, generations.Count, "Two generations should be listed.");
    }

    private static async Task TestManifestCorruptionAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestManifestCorruptionAsync));
        //CSJ,variable=projectPath,プロジェクトのパス
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        //CSJ,variable=service,サービス
        var service = CreateService(root);

        await service.CreateGenerationAsync(projectPath, "first", null);
        //CSJ,variable=manifestPath,目録パス
        var manifestPath = Path.Combine(service.GetProjectDirectory(projectPath), "manifest.json");
        File.WriteAllText(manifestPath, "{ broken json");

        try
        {
            await service.GetGenerationsAsync(projectPath);
            throw new InvalidOperationException("Corrupted manifest must not appear as an empty list.");
        }
        catch (InvalidDataException) { }
        //CSJ,variable=diagnostics,診断情報
        var diagnostics = await service.GetDiagnosticsAsync(projectPath);
        AssertEx.True(diagnostics.ManifestStatus is ProjectGenerationManifestStatus.Corrupted, "Corrupted manifest should be reported.");
    }

    private static async Task TestMetadataCorruptionAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestMetadataCorruptionAsync));
        //CSJ,variable=projectPath,プロジェクトのパス
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        //CSJ,variable=service,サービス
        var service = CreateService(root);

        await service.CreateGenerationAsync(projectPath, "first", null);
        var second = await service.CreateGenerationAsync(projectPath, "second", null);
        //CSJ,variable=metadataPath,メタデータパス
        var metadataPath = Path.Combine(service.GetProjectDirectory(projectPath), "generations", second.GenerationId, "metadata.json");
        File.Delete(metadataPath);

        //CSJ,variable=generations,世代
        var generations = await service.GetGenerationsAsync(projectPath);
        AssertEx.Equal(2, generations.Count, "Corrupted metadata should not remove the generation entry.");
        AssertEx.True(generations.Any(x => !x.IsValid), "Broken generation should be marked invalid.");
    }

    //CSJ,method=TestRestoreAsync,復元をテストする（非同期）
    private static async Task TestRestoreAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestRestoreAsync));
        //CSJ,variable=projectPath,プロジェクトのパス
        var projectPath = CreateProjectFile(root, "project.ymmp", "original");
        //CSJ,variable=service,サービス
        var service = CreateService(root);

        await service.CreateGenerationAsync(projectPath, "restore", "memo");
        await File.WriteAllTextAsync(projectPath, "changed");

        //CSJ,variable=generations,世代
        var generations = await service.GetGenerationsAsync(projectPath);
        //CSJ,variable=generationId,世代ID
        var generationId = generations[0].GenerationId;
        var (success, errorMessage, _) = await service.RestoreGenerationAsync(projectPath, generationId, GenerationRestoreMode.RestoreToOriginalWithBackup);

        AssertEx.True(success, errorMessage ?? "Restore should succeed.");
        AssertEx.Equal("original", await File.ReadAllTextAsync(projectPath), "Project file should be restored.");

        var backupDir = Path.Combine(root, "AppData", "YMMProjectManager", "Generations", "projects", new ProjectGenerationHashService().ComputeProjectId(projectPath), "restore-backups");
        AssertEx.True(Directory.Exists(backupDir), "Backup directory should exist.");
        AssertEx.True(Directory.GetFiles(backupDir, "*.ymmp").Length > 0, "Backup file should be created.");
    }

    //CSJ,method=TestDeleteAsync,削除をテストする（非同期）
    private static async Task TestDeleteAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestDeleteAsync));
        //CSJ,variable=projectPath,プロジェクトのパス
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        //CSJ,variable=service,サービス
        var service = CreateService(root);

        //CSJ,variable=created,作成済み
        var created = await service.CreateGenerationAsync(projectPath, "delete", null);
        //CSJ,variable=generationDirectory,世代ディレクトリ
        var generationDirectory = Path.Combine(service.GetProjectDirectory(projectPath), "generations", created.GenerationId);

        var (success, errorMessage) = await service.DeleteGenerationAsync(projectPath, created.GenerationId);
        AssertEx.True(success, errorMessage ?? "Delete should succeed.");
        AssertEx.True(!Directory.Exists(generationDirectory), "Generation directory should be moved away.");
        AssertEx.Equal(0, (await service.GetGenerationsAsync(projectPath)).Count, "Deleted generation should no longer be listed.");
    }

    //CSJ,method=TestShaMismatchAsync,SHA不一致をテストする（非同期）
    private static async Task TestShaMismatchAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestShaMismatchAsync));
        //CSJ,variable=projectPath,プロジェクトのパス
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        //CSJ,variable=service,サービス
        var service = CreateService(root);

        //CSJ,variable=created,作成済み
        var created = await service.CreateGenerationAsync(projectPath, "sha", null);
        //CSJ,variable=generationPath,世代パス
        var generationPath = Path.Combine(service.GetProjectDirectory(projectPath), "generations", created.GenerationId, "project.ymmp");
        await File.WriteAllTextAsync(generationPath, "corrupted");

        var (success, errorMessage, _) = await service.RestoreGenerationAsync(projectPath, created.GenerationId, GenerationRestoreMode.RestoreToOriginalWithBackup);
        AssertEx.True(!success, "Restore should fail for SHA mismatch.");
        AssertEx.True(!string.IsNullOrWhiteSpace(errorMessage), "Failure should include a reason.");
    }

    //CSJ,method=TestMissingGenerationAsync,欠落世代をテストする（非同期）
    private static async Task TestMissingGenerationAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestMissingGenerationAsync));
        //CSJ,variable=projectPath,プロジェクトのパス
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        //CSJ,variable=service,サービス
        var service = CreateService(root);

        var (success, errorMessage, _) = await service.RestoreGenerationAsync(projectPath, "missing-generation", GenerationRestoreMode.RestoreToOriginalWithBackup);
        AssertEx.True(!success, "Missing generation should fail.");
        AssertEx.True(!string.IsNullOrWhiteSpace(errorMessage), "Missing generation should provide a reason.");
    }

    private static async Task TestLockedFileRestoreFailureAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestLockedFileRestoreFailureAsync));
        //CSJ,variable=projectPath,プロジェクトのパス
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        //CSJ,variable=service,サービス
        var service = CreateService(root);

        //CSJ,variable=created,作成済み
        var created = await service.CreateGenerationAsync(projectPath, "locked", null);
        await File.WriteAllTextAsync(projectPath, "changed");

        //CSJ,variable=lockStream,ロックストリーム
        using var lockStream = new FileStream(projectPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var (success, errorMessage, _) = await service.RestoreGenerationAsync(projectPath, created.GenerationId, GenerationRestoreMode.RestoreToOriginalWithBackup);

        AssertEx.True(!success, "Restore should fail when target file is locked.");
        AssertEx.True(!string.IsNullOrWhiteSpace(errorMessage), "Locked restore should provide a reason.");
    }

    private static async Task TestDeletedFolderExistsAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestDeletedFolderExistsAsync));
        //CSJ,variable=projectPath,プロジェクトのパス
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        //CSJ,variable=service,サービス
        var service = CreateService(root);

        //CSJ,variable=created,作成済み
        var created = await service.CreateGenerationAsync(projectPath, "delete", null);
        await service.DeleteGenerationAsync(projectPath, created.GenerationId);

        //CSJ,variable=deletedPath,削除済みのパス
        var deletedPath = Path.Combine(service.GetProjectDirectory(projectPath), "deleted");
        AssertEx.True(Directory.Exists(deletedPath), "Deleted folder should exist after deletion.");
        AssertEx.True(Directory.EnumerateDirectories(deletedPath).Any(), "Deleted folder should contain moved generations.");
    }

    //CSJ,method=TestDiagnosticsAsync,診断情報をテストする（非同期）
    private static async Task TestDiagnosticsAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestDiagnosticsAsync));
        //CSJ,variable=projectPath,プロジェクトのパス
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        //CSJ,variable=service,サービス
        var service = CreateService(root);

        await service.CreateGenerationAsync(projectPath, "diag", null);
        //CSJ,variable=diagnostics,診断情報
        var diagnostics = await service.GetDiagnosticsAsync(projectPath);

        AssertEx.Equal(1, diagnostics.GenerationCount, "Diagnostics should count valid generations.");
        AssertEx.True(!string.IsNullOrWhiteSpace(diagnostics.ProjectId), "ProjectId should be populated.");
        AssertEx.True(diagnostics.StorageSize > 0, "StorageSize should be populated.");
        AssertEx.True(diagnostics.LatestGeneration is not null, "LatestGeneration should be populated.");
    }

    private static Task TestFastThumbnailOptionsDefaultsAsync()
    {
        //CSJ,variable=options,オプション
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
        //CSJ,variable=frames,フレーム列
        var frames = FastThumbnailFrameSampler.CreateSampleFrames(64, 0, 63);

        AssertEx.Equal(64, frames.Length, "Sample count should be preserved.");
        AssertEx.Equal(0, frames[0], "First sample should be the first frame.");
        AssertEx.Equal(63, frames[^1], "Last sample should be the last frame.");
        AssertEx.True(frames.Zip(frames.Skip(1)).All(pair => pair.First <= pair.Second), "Samples should be monotonic.");
        return Task.CompletedTask;
    }

    //CSJ,method=TestFastThumbnailResultSerializationAsync,高速サムネイル結果シリアル化をテストする（非同期）
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
        //CSJ,variable=restored,復元済み
        var restored = JsonSerializer.Deserialize<FastThumbnailGenerationResult>(json);

        //CSJ,variable=restoredValue,復元済み値
        var restoredValue = restored ?? throw new InvalidOperationException("Result should deserialize.");
        //CSJ,variable=diagnostics,診断情報
        var diagnostics = restoredValue.Diagnostics ?? throw new InvalidOperationException("Diagnostics should deserialize.");

        AssertEx.True(restoredValue.Success, "Success should round-trip.");
        AssertEx.Equal(64, restoredValue.RequestedSampleCount, "Requested sample count should round-trip.");
        AssertEx.Equal(64, restoredValue.CapturedCount, "Captured count should round-trip.");
        AssertEx.Equal(64, diagnostics.SampleCount, "Diagnostics sample count should round-trip.");
        return Task.CompletedTask;
    }

    private static Task TestThumbnailFastGenerationBenchmarkOptionsDefaultsAsync()
    {
        //CSJ,variable=options,オプション
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

    //CSJ,method=TestThumbnailFastGenerationBenchmarkResultSerializationAsync,サムネイル高速世代性能計測結果シリアル化をテストする（非同期）
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
        //CSJ,variable=restored,復元済み
        var restored = JsonSerializer.Deserialize<ThumbnailFastGenerationBenchmarkResult>(json);
        //CSJ,variable=restoredValue,復元済み値
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
        //CSJ,variable=report,報告
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
        //CSJ,variable=restored,復元済み
        var restored = JsonSerializer.Deserialize<ThumbnailFastGenerationBenchmarkSummaryReport>(json);
        //CSJ,variable=restoredValue,復元済み値
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
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestThumbnailFastGenerationBenchmarkRunnerFileGenerationAsync));
        //CSJ,variable=logger,ロガー
        var logger = new FileLogger(Path.Combine(root, "logs", "test.log"));
        //CSJ,variable=runner,実行器
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

    //CSJ,method=TestPreviewBitmapDiagnosticsResultSerializationAsync,プレビュービットマップ診断情報結果シリアル化をテストする（非同期）
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
        //CSJ,variable=restored,復元済み
        var restored = JsonSerializer.Deserialize<PreviewBitmapDiagnosticsResult>(json);
        //CSJ,variable=restoredValue,復元済み値
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

    //CSJ,method=TestPreviewBitmapMethodSignatureSerializationAsync,プレビュービットマップメソッドシグネチャシリアル化をテストする（非同期）
    private static Task TestPreviewBitmapMethodSignatureSerializationAsync()
    {
        //CSJ,variable=signature,シグネチャ
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
        //CSJ,variable=restored,復元済み
        var restored = JsonSerializer.Deserialize<PreviewBitmapMethodSignatureInfo>(json);
        //CSJ,variable=restoredValue,復元済み値
        var restoredValue = restored ?? throw new InvalidOperationException("Result should deserialize.");

        AssertEx.Equal("GetBitmap", restoredValue.MethodName, "MethodName should round-trip.");
        AssertEx.Equal(2, restoredValue.ParameterCount, "ParameterCount should round-trip.");
        AssertEx.Equal("RequiredParameters", restoredValue.Category, "Category should round-trip.");
        AssertEx.Equal(2, restoredValue.InvocationCandidates.Count, "InvocationCandidates should round-trip.");
        return Task.CompletedTask;
    }

    //CSJ,method=TestPreviewBitmapCaptureResultSerializationAsync,プレビュービットマップキャプチャ結果シリアル化をテストする（非同期）
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
        //CSJ,variable=restored,復元済み
        var restored = JsonSerializer.Deserialize<PreviewBitmapCaptureResult>(json);
        //CSJ,variable=restoredValue,復元済み値
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

    //CSJ,method=TestPreviewBitmapComparisonResultSerializationAsync,プレビュービットマップ比較結果シリアル化をテストする（非同期）
    private static Task TestPreviewBitmapComparisonResultSerializationAsync()
    {
        //CSJ,variable=comparison,比較
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
        //CSJ,variable=restored,復元済み
        var restored = JsonSerializer.Deserialize<PreviewBitmapComparisonResult>(json);
        //CSJ,variable=restoredValue,復元済み値
        var restoredValue = restored ?? throw new InvalidOperationException("Result should deserialize.");

        AssertEx.True(restoredValue.FalseSucceeded, "FalseSucceeded should round-trip.");
        AssertEx.Equal("GetBitmap(false)", restoredValue.PreferredCall, "PreferredCall should round-trip.");
        AssertEx.Equal("Smaller image size and identical dimensions.", restoredValue.Reason, "Reason should round-trip.");
        return Task.CompletedTask;
    }

    private static async Task TestPreviewBitmapDiagnosticsNoDispatcherAsync()
    {
        //CSJ,variable=logger,ロガー
        var logger = new FileLogger(Path.Combine(Path.GetTempPath(), "YMMProjectManager-tests", Guid.NewGuid().ToString("N"), "logs", "test.log"));
        //CSJ,variable=service,サービス
        var service = new PreviewBitmapDiagnostics(logger);
        var result = await service.RunAsync(CancellationToken.None);

        AssertEx.True(!result.DiscoverySucceeded, "Diagnostics should fail gracefully without a WPF dispatcher.");
        AssertEx.True(!result.GetBitmapInvocationSucceeded, "Invocation should not run without a dispatcher.");
        AssertEx.True(!string.IsNullOrWhiteSpace(result.FailureReason), "Diagnostics failure should include a reason.");
    }

    private static async Task TestSeekAdapterReflectionFailureAsync()
    {
        //CSJ,variable=adapter,アダプター
        var adapter = new YmmTimelineSeekAdapter();
        var result = await adapter.SeekAsync(new object(), 10, CancellationToken.None);

        AssertEx.True(!result.Success, "Seek should fail gracefully for unsupported objects.");
        AssertEx.True(!string.IsNullOrWhiteSpace(result.Reason), "Seek failure should include a reason.");
    }

    private static async Task TestSeekAdapterUsesAbsoluteTargetFrameAsync()
    {
        var result = await WpfTestHost.RunAsync(async () =>
        {
            //CSJ,variable=adapter,アダプター
            var adapter = new YmmTimelineSeekAdapter();
            //CSJ,variable=timeline,タイムライン
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
            //CSJ,variable=adapter,アダプター
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
            //CSJ,variable=adapter,アダプター
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
            //CSJ,variable=adapter,アダプター
            var adapter = new YmmTimelineSeekAdapter();
            //CSJ,variable=timeline,タイムライン
            var timeline = new FakeTimeline(length: 100) { CurrentFrame = 3 };
            //CSJ,variable=window,ウィンドウ
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
            //CSJ,variable=adapter,アダプター
            var adapter = new YmmTimelineSeekAdapter();
            //CSJ,variable=timeline,タイムライン
            var timeline = new CommandOnlyTimeline(initialFrame: 10);
            //CSJ,variable=command,コマンド
            var command = new RoutedUICommand("Seek", "Seek", typeof(Program));
            //CSJ,variable=window,ウィンドウ
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

    //CSJ,method=TestSeekResultSerializationAsync,フレーム移動結果シリアル化をテストする（非同期）
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
        //CSJ,variable=restored,復元済み
        var restored = JsonSerializer.Deserialize<SeekResult>(json);
        //CSJ,variable=restoredValue,復元済み値
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

    //CSJ,method=TestSeekProbeWriterAsync,フレーム移動調査書き込み処理をテストする（非同期）
    private static async Task TestSeekProbeWriterAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestSeekProbeWriterAsync));
        //CSJ,variable=outputDirectory,出力ディレクトリ
        var outputDirectory = Path.Combine(root, "seek-probe");
        //CSJ,variable=path,パス
        string path = string.Empty;

        await WpfTestHost.RunAsync(async () =>
        {
            //CSJ,variable=adapter,アダプター
            var adapter = new YmmTimelineSeekAdapter();
            //CSJ,variable=timeline,タイムライン
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
            //CSJ,variable=adapter,アダプター
            var adapter = new YmmTimelineSeekAdapter();
            //CSJ,variable=timeline,タイムライン
            var timeline = new CommandOnlyTimeline(initialFrame: 2);
            //CSJ,variable=window,ウィンドウ
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

    //CSJ,method=TestPreviewCaptureFallbackAsync,プレビューキャプチャ代替をテストする（非同期）
    private static async Task TestPreviewCaptureFallbackAsync()
    {
        //CSJ,variable=adapter,アダプター
        var adapter = new YmmPreviewBitmapCaptureAdapter();
        var result = await adapter.TryCaptureAsync(CancellationToken.None);

        AssertEx.True(!result.Success, "Preview capture should fail gracefully when no preview VM is available.");
        AssertEx.True(!string.IsNullOrWhiteSpace(result.FailureReason), "Preview capture failure should include a reason.");
    }

    //CSJ,method=TestCurrentPreviewCaptureResultSerializationAsync,現在のプレビューキャプチャ結果シリアル化をテストする（非同期）
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
        //CSJ,variable=restored,復元済み
        var restored = JsonSerializer.Deserialize<CurrentPreviewCaptureResult>(json);
        //CSJ,variable=restoredValue,復元済み値
        var restoredValue = restored ?? throw new InvalidOperationException("Result should deserialize.");

        AssertEx.True(restoredValue.Success, "Success should round-trip.");
        AssertEx.Equal(17, restoredValue.WindowCount, "WindowCount should round-trip.");
        AssertEx.True(restoredValue.InvocationSucceeded, "InvocationSucceeded should round-trip.");
        AssertEx.Equal("GetBitmap(true)", restoredValue.NextRecommendedCall, "NextRecommendedCall should round-trip.");
        AssertEx.Equal(1920, restoredValue.BitmapWidth ?? 0, "BitmapWidth should round-trip.");
        AssertEx.Equal(@"C:\Temp\current-preview.png", restoredValue.SavedPath, "SavedPath should round-trip.");
        return Task.CompletedTask;
    }

    //CSJ,method=TestCurrentPreviewCapturePreviewViewModelFallbackAsync,現在のプレビューキャプチャプレビュービューモデル代替をテストする（非同期）
    private static async Task TestCurrentPreviewCapturePreviewViewModelFallbackAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestCurrentPreviewCapturePreviewViewModelFallbackAsync));
        //CSJ,variable=logger,ロガー
        var logger = new FileLogger(Path.Combine(root, "logs", "test.log"));
        //CSJ,variable=service,サービス
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

    //CSJ,method=TestCurrentPreviewCaptureGetBitmapFallbackAsync,現在のプレビューキャプチャ取得ビットマップ代替をテストする（非同期）
    private static async Task TestCurrentPreviewCaptureGetBitmapFallbackAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestCurrentPreviewCaptureGetBitmapFallbackAsync));
        //CSJ,variable=logger,ロガー
        var logger = new FileLogger(Path.Combine(root, "logs", "test.log"));
        //CSJ,variable=service,サービス
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

    //CSJ,method=TestLegacyProjectStoreCompatibilityAsync,旧形式プロジェクト保管先互換性をテストする（非同期）
    private static async Task TestLegacyProjectStoreCompatibilityAsync(string workRoot)
    {
        //CSJ,variable=runtimeRoot,実行環境ルート
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
            //CSJ,variable=jsonPath,JSONのパス
            var jsonPath = Path.Combine(dataDir, "projects.json");
            await File.WriteAllTextAsync(jsonPath, json);

            //CSJ,variable=logger,ロガー
            var logger = new FileLogger(Path.Combine(runtimeRoot, "logs", "test.log"));
            //CSJ,variable=repository,リポジトリ
            var repository = new JsonProjectRepository(logger);
            //CSJ,variable=store,保管先
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

    //CSJ,method=TestProjectEntryThumbnailCacheDirectoryNotificationAsync,プロジェクト項目サムネイルキャッシュディレクトリ通知をテストする（非同期）
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

    //CSJ,method=TestProjectGenerationStorageReplaceAsync,プロジェクト世代保管置換をテストする（非同期）
    private static async Task TestProjectGenerationStorageReplaceAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = CreateRoot(workRoot, nameof(TestProjectGenerationStorageReplaceAsync));
        //CSJ,variable=storage,保管
        var storage = new ProjectGenerationStorage(Path.Combine(root, "store"));
        //CSJ,variable=targetDirectory,対象ディレクトリ
        var targetDirectory = Path.Combine(root, "target");
        Directory.CreateDirectory(targetDirectory);

        //CSJ,variable=targetPath,対象パス
        var targetPath = Path.Combine(targetDirectory, "project.ymmp");
        await File.WriteAllTextAsync(targetPath, "old");

        //CSJ,variable=sourceTempPath,元一時パス
        var sourceTempPath = Path.Combine(root, "source.tmp");
        await File.WriteAllTextAsync(sourceTempPath, "new");

        await storage.ReplaceFileAtomicallyAsync(sourceTempPath, targetPath, null);

        AssertEx.Equal("new", await File.ReadAllTextAsync(targetPath), "Atomic replace should overwrite the target content.");
        AssertEx.True(!File.Exists(sourceTempPath), "Atomic replace should consume the source temp file.");
    }

    //CSJ,method=CreateService,サービスを作成する
    private static ProjectGenerationService CreateService(string root)
    {
        //CSJ,variable=logger,ロガー
        var logger = new FileLogger(Path.Combine(root, "logs", "test.log"));
        //CSJ,variable=storageRoot,保管ルート
        var storageRoot = Path.Combine(root, "AppData", "YMMProjectManager", "Generations");
        return new ProjectGenerationService(logger, storageRoot);
    }

    //CSJ,method=CreateRoot,ルートを作成する
    private static string CreateRoot(string workRoot, string testName)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(workRoot, testName);
        Directory.CreateDirectory(root);
        return root;
    }

    //CSJ,method=CreateProjectFile,プロジェクトファイルを作成する
    private static string CreateProjectFile(string root, string fileName, string content)
    {
        //CSJ,variable=path,パス
        var path = Path.Combine(root, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    //CSJ,class=TimelineContainer,タイムラインの格納先
    private sealed class TimelineContainer
    {
        //CSJ,property=Timeline,タイムライン
        public object? Timeline { get; init; }
    }

    //CSJ,class=FakeTimeline,テスト用タイムライン
    private sealed class FakeTimeline
    {
        public FakeTimeline(int length)
        {
            Length = length;
        }

        //CSJ,property=Length,長さ
        public int Length { get; }

        //CSJ,property=CurrentFrame,現在のフレーム
        public int CurrentFrame { get; set; }
    }

    //CSJ,class=ThrowingTimeline,例外を送出するテスト用タイムライン
    private sealed class ThrowingTimeline
    {
        //CSJ,property=CurrentFrame,現在のフレーム
        public int CurrentFrame
        {
            get => 10;
            set => throw new InvalidOperationException("setter boom");
        }
    }

    //CSJ,class=NonMovingTimeline,移動しないテスト用タイムライン
    private sealed class NonMovingTimeline
    {
        //CSJ,field=currentFrame,現在のフレーム
        private int currentFrame;

        public NonMovingTimeline(int initialFrame)
        {
            currentFrame = initialFrame;
        }

        //CSJ,property=CurrentFrame,現在のフレーム
        public int CurrentFrame
        {
            get => currentFrame;
            set
            {
            }
        }
    }

    //CSJ,class=CommandOnlyTimeline,コマンドのみのテスト用タイムライン
    private sealed class CommandOnlyTimeline
    {
        //CSJ,field=currentFrame,現在のフレーム
        private int currentFrame;

        public CommandOnlyTimeline(int initialFrame)
        {
            currentFrame = initialFrame;
        }

        //CSJ,property=CurrentFrame,現在のフレーム
        public int CurrentFrame => currentFrame;

        public void MoveBy(int delta)
        {
            currentFrame += delta;
        }
    }

    //CSJ,class=FakeTimelineSeekAdapter,テスト用フレーム移動アダプター
    private sealed class FakeTimelineSeekAdapter : ITimelineSeekAdapter
    {
        //CSJ,method=SeekAsync,指定フレームへ移動する（非同期）
        public Task<SeekResult> SeekAsync(object? timeline, int targetFrame, CancellationToken cancellationToken)
        {
            if (timeline is FakeTimeline fakeTimeline)
            {
                fakeTimeline.CurrentFrame = targetFrame;
            }

            return Task.FromResult(SeekResult.Succeeded(targetFrame, targetFrame));
        }
    }

    //CSJ,class=FakePreviewBitmapCaptureAdapter,テスト用プレビュー画像取得アダプター
    private sealed class FakePreviewBitmapCaptureAdapter : IPreviewBitmapCaptureAdapter
    {
        //CSJ,method=TryCaptureAsync,キャプチャを試みる（非同期）
        public Task<PreviewCaptureResult> TryCaptureAsync(CancellationToken cancellationToken)
        {
            //CSJ,variable=pixels,画素列
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

            //CSJ,variable=bitmap,ビットマップ
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

    //CSJ,class=FakeFailingPreviewBitmapCaptureAdapter,取得に失敗するテスト用プレビュー画像アダプター
    private sealed class FakeFailingPreviewBitmapCaptureAdapter : IPreviewBitmapCaptureAdapter
    {
        //CSJ,field=reason,理由
        private readonly string reason;

        public FakeFailingPreviewBitmapCaptureAdapter(string reason)
        {
            this.reason = reason;
        }

        //CSJ,method=TryCaptureAsync,キャプチャを試みる（非同期）
        public Task<PreviewCaptureResult> TryCaptureAsync(CancellationToken cancellationToken)
            => Task.FromResult(PreviewCaptureResult.Failed(reason));
    }

    //CSJ,class=WpfTestHost,WPFテストホスト
    internal static class WpfTestHost
    {
        //CSJ,field=SyncRoot,同期ルート
        private static readonly object SyncRoot = new();
        //CSJ,field=thread,スレッド
        private static Thread? thread;
        //CSJ,field=dispatcher,ディスパッチャー
        private static Dispatcher? dispatcher;
        //CSJ,field=application,アプリケーション
        private static Application? application;

        //CSJ,method=RunAsync,実行する（非同期）
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
                //CSJ,variable=startupException,起動例外
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

    //CSJ,class=AssertEx,テスト検証ヘルパー
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
