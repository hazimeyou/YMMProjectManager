namespace YMMProjectManager.Infrastructure.History;

public sealed class ProjectSnapshotService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly FileLogger logger;
    private readonly JsonNormalizeService normalizeService;
    private readonly string rootDirectory;

    public ProjectSnapshotService(FileLogger logger, JsonNormalizeService normalizeService, ProjectSnapshotOptions? options = null)
    {
        this.logger = logger;
        this.normalizeService = normalizeService;
        rootDirectory = string.IsNullOrWhiteSpace(options?.RootDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YMMProjectManager", "history")
            : options!.RootDirectory;
    }

    public async Task<ProjectSnapshotMetadata?> CreateSnapshotAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPath = Path.GetFullPath(projectPath);
            if (!File.Exists(fullPath))
            {
                return null;
            }

            var projectKey = ProjectHashService.ComputeProjectKey(fullPath);
            var snapshotId = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
            var snapshotDir = Path.Combine(rootDirectory, "projects", projectKey, "snapshots", snapshotId);
            Directory.CreateDirectory(snapshotDir);

            var originalDest = Path.Combine(snapshotDir, "original.ymmp");
            File.Copy(fullPath, originalDest, overwrite: false);

            var normalized = await normalizeService.NormalizeFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var normalizedPath = Path.Combine(snapshotDir, "normalized.json");
            await File.WriteAllTextAsync(normalizedPath, normalized, cancellationToken).ConfigureAwait(false);

            var metadata = new ProjectSnapshotMetadata
            {
                SnapshotId = snapshotId,
                ProjectPath = fullPath,
                ProjectKey = projectKey,
                CreatedAt = DateTimeOffset.Now,
                OriginalFileSize = new FileInfo(fullPath).Length,
                InternalItemIdVersion = 1,
            };

            var metadataPath = Path.Combine(snapshotDir, "metadata.json");
            await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, JsonOptions), cancellationToken).ConfigureAwait(false);
            await EnsureProjectInfoAsync(projectKey, fullPath, cancellationToken).ConfigureAwait(false);
            return metadata;
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"CreateSnapshotAsync failed: {projectPath}");
            return null;
        }
    }

    public async Task<IReadOnlyList<ProjectSnapshotMetadata>> GetSnapshotsAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPath = Path.GetFullPath(projectPath);
            var projectKey = ProjectHashService.ComputeProjectKey(fullPath);
            var snapshotsDir = Path.Combine(rootDirectory, "projects", projectKey, "snapshots");
            if (!Directory.Exists(snapshotsDir))
            {
                return [];
            }

            var snapshots = new List<ProjectSnapshotMetadata>();
            foreach (var dir in Directory.EnumerateDirectories(snapshotsDir))
            {
                var metadataPath = Path.Combine(dir, "metadata.json");
                if (!File.Exists(metadataPath))
                {
                    continue;
                }

                var json = await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false);
                var metadata = JsonSerializer.Deserialize<ProjectSnapshotMetadata>(json);
                if (metadata is not null)
                {
                    snapshots.Add(metadata);
                }
            }

            return snapshots.OrderByDescending(x => x.SnapshotId).ToList();
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"GetSnapshotsAsync failed: {projectPath}");
            return [];
        }
    }

    public Task<bool> DeleteSnapshotAsync(string projectPath, string snapshotId)
    {
        try
        {
            var projectKey = ProjectHashService.ComputeProjectKey(projectPath);
            var dir = Path.Combine(rootDirectory, "projects", projectKey, "snapshots", snapshotId);
            if (!Directory.Exists(dir))
            {
                return Task.FromResult(false);
            }

            Directory.Delete(dir, recursive: true);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"DeleteSnapshotAsync failed: {projectPath} / {snapshotId}");
            return Task.FromResult(false);
        }
    }

    public string? TryGetNormalizedPath(string projectPath, string snapshotId)
    {
        try
        {
            var projectKey = ProjectHashService.ComputeProjectKey(projectPath);
            var path = Path.Combine(rootDirectory, "projects", projectKey, "snapshots", snapshotId, "normalized.json");
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task EnsureProjectInfoAsync(string projectKey, string projectPath, CancellationToken cancellationToken)
    {
        var projectDir = Path.Combine(rootDirectory, "projects", projectKey);
        Directory.CreateDirectory(projectDir);
        var projectInfoPath = Path.Combine(projectDir, "project.json");
        var payload = JsonSerializer.Serialize(new { projectPath }, JsonOptions);
        await File.WriteAllTextAsync(projectInfoPath, payload, cancellationToken).ConfigureAwait(false);
    }
}

