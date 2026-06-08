using System;
using System.IO;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Infrastructure.Generations;

public sealed class ProjectGenerationManifestRepository
{
    private readonly ProjectGenerationStorage storage;
    private readonly FileLogger logger;

    public ProjectGenerationManifestRepository(ProjectGenerationStorage storage, FileLogger logger)
    {
        this.storage = storage;
        this.logger = logger;
    }

    public async Task<ProjectGenerationManifest> LoadOrCreateAsync(string projectId, string projectPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var manifest = await storage.ReadManifestAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (manifest is not null)
            {
                return manifest;
            }
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Failed to load generation manifest. projectId={projectId}");
        }

        var now = DateTimeOffset.Now;
        return new ProjectGenerationManifest
        {
            ProjectId = projectId,
            ProjectPath = projectPath,
            ProjectFileName = Path.GetFileName(projectPath),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public async Task SaveAsync(string projectId, ProjectGenerationManifest manifest, CancellationToken cancellationToken = default)
    {
        try
        {
            await storage.WriteManifestAsync(projectId, manifest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Failed to save generation manifest. projectId={projectId}");
        }
    }
}
