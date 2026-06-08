using System.IO;
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
        await TestCreateAndListAsync(workRoot);
        await TestMultipleGenerationsAsync(workRoot);
        await TestRestoreAsync(workRoot);
        await TestDeleteAsync(workRoot);
        await TestShaMismatchAsync(workRoot);
        await TestMissingGenerationAsync(workRoot);
        await TestLockedFileRestoreFailureAsync(workRoot);
        await TestBrokenMetadataIsToleratedAsync(workRoot);
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

    private static async Task TestBrokenMetadataIsToleratedAsync(string workRoot)
    {
        var root = CreateRoot(workRoot, nameof(TestBrokenMetadataIsToleratedAsync));
        var projectPath = CreateProjectFile(root, "project.ymmp", "alpha");
        var service = CreateService(root);

        var created = await service.CreateGenerationAsync(projectPath, "broken", null);
        var metadataPath = Path.Combine(service.GetProjectDirectory(projectPath), "generations", created.GenerationId, "metadata.json");
        File.Delete(metadataPath);

        var generations = await service.GetGenerationsAsync(projectPath);
        AssertEx.Equal(1, generations.Count, "Missing metadata should not drop the generation from the list.");
        AssertEx.True(!generations[0].IsValid, "Broken generation should be marked invalid.");
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
