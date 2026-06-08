using YMMProjectManager.Domain;

namespace YMMProjectManager.Infrastructure.Generations;

public sealed class ProjectGenerationManifestLoadResult
{
    public ProjectGenerationManifestLoadResult(ProjectGenerationManifest manifest, ProjectGenerationManifestStatus status, string? errorMessage = null)
    {
        Manifest = manifest;
        Status = status;
        ErrorMessage = errorMessage;
    }

    public ProjectGenerationManifest Manifest { get; }
    public ProjectGenerationManifestStatus Status { get; }
    public string? ErrorMessage { get; }
}
