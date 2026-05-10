namespace YMMProjectManager.Application;

public interface IProjectRepository
{
    Task<IReadOnlyList<ProjectEntry>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyList<ProjectEntry> projects, CancellationToken cancellationToken = default);
}

