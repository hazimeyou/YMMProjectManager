using System.IO;
using System.Linq;
using System.Text.Json;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Generations;

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
}
