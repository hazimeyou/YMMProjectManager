using YMMProjectManager.Domain;

namespace YMMProjectManager.Infrastructure.Generations;

public sealed class ProjectGenerationManifestLoadResult
{
    public ProjectGenerationManifestLoadResult(ProjectGenerationManifest? manifest, ProjectGenerationManifestStatus status, string? errorMessage = null, string? contentHash = null)
    {
        Manifest = manifest;
        Status = status;
        ErrorMessage = errorMessage;
        ContentHash = contentHash;
    }

    public ProjectGenerationManifest? Manifest { get; }
    public ProjectGenerationManifestStatus Status { get; }
    public string? ErrorMessage { get; }
    internal string? ContentHash { get; }
}
