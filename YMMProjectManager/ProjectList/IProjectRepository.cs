using System.Threading;
using System.Threading.Tasks;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Application;

public interface IProjectRepository
{
    Task<ProjectStore> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ProjectStore store, CancellationToken cancellationToken = default);
}
