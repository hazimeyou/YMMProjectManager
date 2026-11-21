using System;

namespace YMMProjectManager.Domain;

public sealed class ProjectGenerationDiagnostics
{
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectPath { get; set; } = string.Empty;
    public int GenerationCount { get; set; }
    public long StorageSize { get; set; }
    public string? LatestGeneration { get; set; }
    public ProjectGenerationManifestStatus ManifestStatus { get; set; }
    public int DeletedGenerationCount { get; set; }
    public DateTimeOffset? LatestGenerationCreatedAt { get; set; }
    public string? LatestGenerationDisplayName { get; set; }
}
