using System.Globalization;
using System.IO;
using System.Text.Json;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure.Generations;

namespace YMMProjectManager.Infrastructure.Checkpoint;

public sealed class CheckpointStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string rootDirectory;
    private readonly ProjectGenerationHashService hashService = new();

    public CheckpointStorage(string? rootDirectory = null)
    {
        this.rootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YMMProjectManager", "Checkpoints")
            : rootDirectory;
    }

    public string RootDirectory => rootDirectory;

    public string GetProjectId(string projectPath) => hashService.ComputeProjectId(Path.GetFullPath(projectPath));
    public string GetProjectDirectory(string projectPath) => Path.Combine(rootDirectory, "projects", GetProjectId(projectPath));
    public string GetManifestPath(string projectPath) => Path.Combine(GetProjectDirectory(projectPath), "manifest.json");
    public string GetCheckpointsDirectory(string projectPath) => Path.Combine(GetProjectDirectory(projectPath), "checkpoints");
    public string GetCheckpointDirectory(string projectPath, string checkpointId) => Path.Combine(GetCheckpointsDirectory(projectPath), checkpointId);
    public string GetMetadataPath(string projectPath, string checkpointId) => Path.Combine(GetCheckpointDirectory(projectPath, checkpointId), "metadata.json");
    public string GetYmmpPath(string projectPath, string checkpointId) => Path.Combine(GetCheckpointDirectory(projectPath, checkpointId), "project.ymmp");
    public string GetYmmpxPath(string projectPath, string checkpointId) => Path.Combine(GetCheckpointDirectory(projectPath, checkpointId), "project.ymmpx");
    public string GetThumbnailsDirectory(string projectPath, string checkpointId) => Path.Combine(GetCheckpointDirectory(projectPath, checkpointId), "thumbnails");
    public string GetDeletedDirectory(string projectPath) => Path.Combine(GetProjectDirectory(projectPath), "deleted");
    public string GetDeletedCheckpointDirectory(string projectPath, string checkpointId)
        => Path.Combine(GetDeletedDirectory(projectPath), $"{checkpointId}_{DateTimeOffset.Now:yyyyMMdd-HHmmss}");

    public void EnsureProjectLayout(string projectPath)
    {
        Directory.CreateDirectory(GetProjectDirectory(projectPath));
        Directory.CreateDirectory(GetCheckpointsDirectory(projectPath));
        Directory.CreateDirectory(GetDeletedDirectory(projectPath));
    }

    public async Task WriteManifestAsync(string projectPath, CheckpointManifest manifest, CancellationToken cancellationToken = default)
    {
        await WriteJsonAsync(GetManifestPath(projectPath), manifest, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CheckpointManifest?> ReadManifestAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        return await ReadJsonAsync<CheckpointManifest>(GetManifestPath(projectPath), cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteMetadataAsync(string projectPath, string checkpointId, CheckpointManifestItem item, CancellationToken cancellationToken = default)
    {
        await WriteJsonAsync(GetMetadataPath(projectPath, checkpointId), item, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CheckpointManifestItem?> ReadMetadataAsync(string projectPath, string checkpointId, CancellationToken cancellationToken = default)
    {
        return await ReadJsonAsync<CheckpointManifestItem>(GetMetadataPath(projectPath, checkpointId), cancellationToken).ConfigureAwait(false);
    }

    public async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
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

    public string CreateCheckpointId(DateTimeOffset createdAt)
        => createdAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    public CheckpointPaths ResolveCheckpointPaths(string projectPath, string checkpointId)
    {
        return new CheckpointPaths
        {
            ProjectDirectory = GetProjectDirectory(projectPath),
            ManifestPath = GetManifestPath(projectPath),
            CheckpointDirectory = GetCheckpointDirectory(projectPath, checkpointId),
            MetadataPath = GetMetadataPath(projectPath, checkpointId),
            YmmpPath = GetYmmpPath(projectPath, checkpointId),
            YmmpxPath = GetYmmpxPath(projectPath, checkpointId),
            ThumbnailsDirectory = GetThumbnailsDirectory(projectPath, checkpointId),
        };
    }

    public Task DeleteCheckpointDirectoryAsync(string projectPath, string checkpointId, CancellationToken cancellationToken = default)
    {
        var checkpointDirectory = GetCheckpointDirectory(projectPath, checkpointId);
        var deletedDirectory = GetDeletedCheckpointDirectory(projectPath, checkpointId);
        var deletedParent = Path.GetDirectoryName(deletedDirectory);
        if (!string.IsNullOrWhiteSpace(deletedParent))
        {
            Directory.CreateDirectory(deletedParent);
        }

        return Task.Run(() =>
        {
            if (!Directory.Exists(checkpointDirectory))
            {
                return;
            }

            if (Directory.Exists(deletedDirectory))
            {
                Directory.Delete(deletedDirectory, recursive: true);
            }

            Directory.Move(checkpointDirectory, deletedDirectory);
            Directory.Delete(deletedDirectory, recursive: true);
        }, cancellationToken);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
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
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
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
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class CheckpointPaths
{
    public string ProjectDirectory { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public string CheckpointDirectory { get; set; } = string.Empty;
    public string MetadataPath { get; set; } = string.Empty;
    public string YmmpPath { get; set; } = string.Empty;
    public string YmmpxPath { get; set; } = string.Empty;
    public string ThumbnailsDirectory { get; set; } = string.Empty;
}
