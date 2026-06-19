using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using YMMProjectManager.Application;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Generations;
using YMMProjectManager.Infrastructure.Output;
using YMMProjectManager.Presentation.ViewModels;

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
        await TestLegacyProjectStoreCompatibilityAsync(workRoot);
        await TestProjectEntryThumbnailCacheDirectoryNotificationAsync();
        await TestProjectGenerationStorageReplaceAsync(workRoot);
        await TestProjectListViewModelPreservesFoldersOnSaveAsync(workRoot);
        await TestThumbnailImageLoaderInvalidationAsync(workRoot);
        await TestPreviewGetBitmapPriorityAsync();
        await TestPreviewCaptureResultSerializationAsync();
        await TestPreviewDiscoveryVisualTreeGuardAsync();
        await TestTimelineDurationProbeResultSerializationAsync();
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

    private static async Task TestProjectListViewModelPreservesFoldersOnSaveAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestProjectListViewModelPreservesFoldersOnSaveAsync));
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        var logger = new FileLogger(Path.Combine(root, "logs", "test.log"));
        var repository = new FakeProjectRepository(new ProjectStore
        {
            Folders =
            [
                new ProjectFolder
                {
                    Id = Guid.NewGuid(),
                    Name = "Favorites",
                    DisplayOrder = 1,
                }
            ],
        });

        var ctor = typeof(ProjectListViewModel).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(FileLogger), typeof(IProjectRepository)],
            modifiers: null);

        AssertEx.True(ctor is not null, "View model constructor should be discoverable for tests.");
        var vm = (ProjectListViewModel)ctor!.Invoke([logger, repository]);

        await vm.InitializeAsync();
        await vm.AddProjectsAsync([projectPath]);

        AssertEx.True(repository.LastSavedStore is not null, "Repository should receive a saved store.");
        AssertEx.Equal(1, repository.LastSavedStore!.Folders.Count, "Loaded folders should survive view-model save.");
        AssertEx.Equal("Favorites", repository.LastSavedStore.Folders[0].Name, "Folder name should round-trip.");
        AssertEx.Equal(1, repository.LastSavedStore.Projects.Count, "Project addition should still be saved.");
    }

    private static async Task TestThumbnailImageLoaderInvalidationAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestThumbnailImageLoaderInvalidationAsync));
        var logger = new FileLogger(Path.Combine(root, "logs", "test.log"));
        var thumbnailPath = Path.Combine(root, "thumb.png");

        var missing = await ThumbnailImageLoader.LoadAsync(thumbnailPath, logger);
        AssertEx.True(missing is null, "Missing thumbnail should return null.");

        ThumbnailSequenceFrameRenderer.SaveBlankPng(thumbnailPath);

        var loaded = await ThumbnailImageLoader.LoadAsync(thumbnailPath, logger);
        AssertEx.True(loaded is not null, "Thumbnail loader should retry after a file appears.");
    }

    private static Task TestPreviewGetBitmapPriorityAsync()
    {
        var fake = new FakePreviewViewModel();
        var found = YMMProjectManager.Infrastructure.Output.YmmPreviewBitmapCaptureAdapter.TrySelectGetBitmapMethod(
            fake,
            out var method,
            out var parameterTypes,
            out var nextRecommendedCall);

        AssertEx.True(found, "GetBitmap overload should be discovered.");
        AssertEx.True(method is not null, "GetBitmap method should be selected.");
        AssertEx.Equal("GetBitmap", method!.Name, "Selected method name should be GetBitmap.");
        AssertEx.Equal("System.Boolean", parameterTypes[0], "Boolean overload should be preferred.");
        AssertEx.Equal("GetBitmap(true)", nextRecommendedCall, "Recommended call should prefer true.");
        return Task.CompletedTask;
    }

    private static Task TestPreviewCaptureResultSerializationAsync()
    {
        var result = new YMMProjectManager.Infrastructure.Output.CurrentPreviewCaptureResult
        {
            Timestamp = new DateTimeOffset(2026, 6, 10, 12, 34, 56, TimeSpan.FromHours(9)),
            Success = true,
            FailureReason = null,
            WindowCount = 2,
            VisualTreeElementCount = 11,
            PreviewViewFound = true,
            PreviewViewModelFound = true,
            GetBitmapMethodFound = true,
            GetBitmapParameterTypes = ["System.Boolean"],
            NextRecommendedCall = "GetBitmap(true)",
            CaptureSucceeded = true,
            BitmapWidth = 1280,
            BitmapHeight = 720,
            BitmapPixelFormat = "Bgra32",
            SavedPath = @"C:\Temp\preview.png",
            DurationMs = 42,
        };

        var json = JsonSerializer.Serialize(result);
        var roundTrip = JsonSerializer.Deserialize<YMMProjectManager.Infrastructure.Output.CurrentPreviewCaptureResult>(json);

        AssertEx.True(roundTrip is not null, "Preview capture result should deserialize.");
        AssertEx.True(roundTrip!.Success, "Preview capture result should preserve success.");
        AssertEx.Equal("GetBitmap(true)", roundTrip.NextRecommendedCall, "Preview capture result should preserve recommended call.");
        AssertEx.Equal(1280, roundTrip.BitmapWidth, "Preview capture result should preserve width.");
        return Task.CompletedTask;
    }

    private static Task TestPreviewDiscoveryVisualTreeGuardAsync()
    {
        var nonVisual = new System.Windows.Media.TranslateTransform();
        AssertEx.True(!YMMProjectManager.Infrastructure.Output.YmmPreviewDiscoveryService.CanUseVisualTree(nonVisual), "Non-visual dependency objects should not enter VisualTreeHelper traversal.");
        return Task.CompletedTask;
    }

    private static Task TestTimelineDurationProbeResultSerializationAsync()
    {
        var result = new YMMProjectManager.Infrastructure.Output.TimelineDurationProbeResult
        {
            Success = true,
            CurrentFrame = 123,
            LastFrame = 456,
            MethodUsed = "Timeline.Length",
            FailureReason = null,
            CandidateProperties = ["Timeline.Length"],
            DurationMs = 21,
        };

        var json = JsonSerializer.Serialize(result);
        var roundTrip = JsonSerializer.Deserialize<YMMProjectManager.Infrastructure.Output.TimelineDurationProbeResult>(json);

        AssertEx.True(roundTrip is not null, "Timeline duration result should deserialize.");
        AssertEx.True(roundTrip!.Success, "Timeline duration result should preserve success.");
        AssertEx.Equal(456, roundTrip.LastFrame, "Timeline duration result should preserve last frame.");
        AssertEx.Equal("Timeline.Length", roundTrip.MethodUsed, "Timeline duration result should preserve method.");
        return Task.CompletedTask;
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

    private sealed class FakePreviewViewModel
    {
        public object GetBitmap(bool useHighQuality) => new { HighQuality = useHighQuality };

        public object GetBitmap() => new { HighQuality = false };
    }

    private sealed class FakeProjectRepository : IProjectRepository
    {
        private readonly ProjectStore initialStore;

        public FakeProjectRepository(ProjectStore initialStore)
        {
            this.initialStore = initialStore;
        }

        public ProjectStore? LastSavedStore { get; private set; }

        public Task<ProjectStore> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ProjectStore
            {
                Projects = initialStore.Projects.ToList(),
                Folders = initialStore.Folders.ToList(),
            });
        }

        public Task SaveAsync(ProjectStore store, CancellationToken cancellationToken = default)
        {
            LastSavedStore = new ProjectStore
            {
                Projects = store.Projects.ToList(),
                Folders = store.Folders.ToList(),
            };
            return Task.CompletedTask;
        }
    }

}
