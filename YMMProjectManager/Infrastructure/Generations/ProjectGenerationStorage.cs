using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Infrastructure.Generations;

public sealed class ProjectGenerationStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string rootDirectory;

    public ProjectGenerationStorage(string? rootDirectory = null)
    {
        this.rootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "YMMProjectManager",
                "Generations")
            : rootDirectory;
    }

    public string RootDirectory => rootDirectory;

    public string GetProjectDirectory(string projectId) => Path.Combine(rootDirectory, "projects", projectId);

    public string GetManifestPath(string projectId) => Path.Combine(GetProjectDirectory(projectId), "manifest.json");

    public string GetGenerationsDirectory(string projectId) => Path.Combine(GetProjectDirectory(projectId), "generations");

    public string GetDeletedDirectory(string projectId) => Path.Combine(GetProjectDirectory(projectId), "deleted");

    public string GetRestoreBackupsDirectory(string projectId) => Path.Combine(GetProjectDirectory(projectId), "restore-backups");

    public string GetGenerationDirectory(string projectId, string generationId) => Path.Combine(GetGenerationsDirectory(projectId), generationId);

    public string GetGenerationFilePath(string projectId, string generationId) => Path.Combine(GetGenerationDirectory(projectId, generationId), "project.ymmp");

    public string GetGenerationMetadataPath(string projectId, string generationId) => Path.Combine(GetGenerationDirectory(projectId, generationId), "metadata.json");

    public string GetDeletedGenerationDirectory(string projectId, string generationId)
    {
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(GetDeletedDirectory(projectId), $"{generationId}_{timestamp}");
    }

    public async Task WriteManifestAsync(string projectId, ProjectGenerationManifest manifest, CancellationToken cancellationToken = default)
    {
        var path = GetManifestPath(projectId);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path, overwrite: true);
            }
        }
        finally
        {
            DeleteFileIfExists(tempPath);
        }
    }

    public async Task<ProjectGenerationManifest?> ReadManifestAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var path = GetManifestPath(projectId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ProjectGenerationManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteMetadataAsync(string projectId, string generationId, ProjectGenerationManifestItem metadata, CancellationToken cancellationToken = default)
    {
        var path = GetGenerationMetadataPath(projectId, generationId);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, metadata, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path, overwrite: true);
            }
        }
        finally
        {
            DeleteFileIfExists(tempPath);
        }
    }

    public async Task<ProjectGenerationManifestItem?> ReadMetadataAsync(string projectId, string generationId, CancellationToken cancellationToken = default)
    {
        var path = GetGenerationMetadataPath(projectId, generationId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ProjectGenerationManifestItem>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public void EnsureProjectLayout(string projectId)
    {
        Directory.CreateDirectory(GetProjectDirectory(projectId));
        Directory.CreateDirectory(GetGenerationsDirectory(projectId));
        Directory.CreateDirectory(GetDeletedDirectory(projectId));
        Directory.CreateDirectory(GetRestoreBackupsDirectory(projectId));
    }

    public async Task CopyProjectSnapshotAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var source = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceFileAtomicallyAsync(string sourceTempPath, string targetPath, string? backupPath = null, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(targetPath))
        {
            if (!string.IsNullOrWhiteSpace(backupPath))
            {
                var backupDirectory = Path.GetDirectoryName(backupPath);
                if (!string.IsNullOrWhiteSpace(backupDirectory))
                {
                    Directory.CreateDirectory(backupDirectory);
                }
                File.Replace(sourceTempPath, targetPath, backupPath, ignoreMetadataErrors: true);
                return;
            }

            File.Move(sourceTempPath, targetPath, overwrite: true);
            return;
        }

        File.Move(sourceTempPath, targetPath, overwrite: true);
    }

    public async Task<string> CreateTemporaryCopyAsync(string sourcePath, string targetDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(targetDirectory);
        var tempPath = Path.Combine(targetDirectory, $"{Guid.NewGuid():N}.tmp");
        await CopyProjectSnapshotAsync(sourcePath, tempPath, cancellationToken).ConfigureAwait(false);
        return tempPath;
    }

    public string ResolveRestoreBackupPath(string projectId, string targetProjectPath)
    {
        var projectFileName = Path.GetFileName(targetProjectPath);
        return Path.Combine(GetRestoreBackupsDirectory(projectId), $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}_before-restore{Path.GetExtension(projectFileName)}");
    }

    public async Task MoveDirectoryAsync(string sourceDirectory, string destinationDirectory, CancellationToken cancellationToken = default)
    {
        var destinationParent = Path.GetDirectoryName(destinationDirectory);
        if (!string.IsNullOrWhiteSpace(destinationParent))
        {
            Directory.CreateDirectory(destinationParent);
        }

        if (Directory.Exists(destinationDirectory))
        {
            Directory.Delete(destinationDirectory, recursive: true);
        }

        await Task.Run(() => Directory.Move(sourceDirectory, destinationDirectory), cancellationToken).ConfigureAwait(false);
    }

    public long GetDirectorySizeBytes(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch
            {
            }
        }

        return total;
    }

    public int CountChildDirectories(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateDirectories(directoryPath).Count();
        }
        catch
        {
            return 0;
        }
    }

    public void DeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
