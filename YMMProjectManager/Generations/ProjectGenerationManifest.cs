using System;
using System.Collections.Generic;

namespace YMMProjectManager.Domain;

public sealed class ProjectGenerationManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectPath { get; set; } = string.Empty;
    public string ProjectFileName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ProjectGenerationManifestItem> Generations { get; set; } = [];
}
