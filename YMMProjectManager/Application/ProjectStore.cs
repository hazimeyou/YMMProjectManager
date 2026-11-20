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
    internal string? PreservedFieldAnchors { get; init; }
}

internal static class ProjectListFieldAnchors
{
    internal static string Project(Guid id) => $"Project:{id:N}";
    internal static string Folder(Guid id) => $"Folder:{id:N}";
    internal static string Link(Guid projectId, Guid id) => $"Link:{projectId:N}/{id:N}";

    internal static IEnumerable<string> Enumerate(ProjectStore store)
    {
        foreach (var project in store.Projects)
        {
            if (project.ProjectId is not { } id) continue;
            yield return Project(id);
            foreach (var link in project.LinkedYmmpFiles) yield return Link(id, link.Id);
        }
        foreach (var folder in store.Folders) yield return Folder(folder.Id);
    }
}
