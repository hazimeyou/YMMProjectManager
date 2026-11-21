using System;

namespace YMMProjectManager.Domain;

public sealed class ProjectGenerationManifestItem
{
    public string GenerationId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Memo { get; set; }
    public string SourceProjectPath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string CreatedByVersion { get; set; } = string.Empty;
    public string? DeletedAt { get; set; }
}
