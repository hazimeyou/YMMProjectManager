using System.Collections.Generic;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Application;

public sealed class ProjectStore
{
    public List<ProjectEntry> Projects { get; set; } = [];
    public List<ProjectFolder> Folders { get; set; } = [];
}
