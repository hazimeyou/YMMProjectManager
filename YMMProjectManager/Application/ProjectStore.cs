using System.Collections.Generic;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Application;

public sealed class ProjectStore
{
    public List<ProjectEntry> Projects { get; set; } = [];
    public List<ProjectFolder> Folders { get; set; } = [];
    public ProjectListRevision? Revision { get; set; }
    public bool IsRecoverySafe { get; set; }
}

public sealed record ProjectListRevision(string? Sha256, Guid? OperationId = null)
{
    // Opaque persistence data for forward-compatible root properties.
    internal string? PreservedRootFieldsJson { get; init; }
    internal string? PreservedNestedFieldsJson { get; init; }
}
