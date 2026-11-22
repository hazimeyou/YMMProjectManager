using System.Threading;
using System.Threading.Tasks;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Application;

//CSJ,interface=IProjectRepository,プロジェクト一覧の保存先
public interface IProjectRepository
{
    //CSJ,method=LoadAsync,プロジェクト一覧を読み込む
    Task<ProjectStore> LoadAsync(CancellationToken cancellationToken = default);
    //CSJ,method=SaveAsync,プロジェクト一覧を保存する
    Task SaveAsync(ProjectStore store, CancellationToken cancellationToken = default);
}
