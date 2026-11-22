using YMMProjectManager.Domain;

namespace YMMProjectManager.Application;

public interface IProjectGenerationService
{
    Task<ProjectGenerationRecord> CreateGenerationAsync(string projectPath, string displayName, string? memo, CancellationToken cancellationToken = default);
    //CSJ,method=GetGenerationsAsync,世代を取得する（非同期）
    Task<IReadOnlyList<ProjectGenerationRecord>> GetGenerationsAsync(string projectPath, CancellationToken cancellationToken = default);
    //CSJ,method=GetDiagnosticsAsync,診断情報を取得する（非同期）
    Task<ProjectGenerationDiagnostics> GetDiagnosticsAsync(string projectPath, CancellationToken cancellationToken = default);
    Task<(bool Success, string? ErrorMessage, ProjectGenerationRecord? Generation)> RestoreGenerationAsync(string projectPath, string generationId, GenerationRestoreMode restoreMode, CancellationToken cancellationToken = default);
    //CSJ,method=DeleteGenerationAsync,世代を削除する（非同期）
    Task<(bool Success, string? ErrorMessage)> DeleteGenerationAsync(string projectPath, string generationId, CancellationToken cancellationToken = default);
    //CSJ,method=GetRootDirectory,ルートディレクトリを取得する
    string GetRootDirectory();
    //CSJ,method=GetProjectDirectory,プロジェクトディレクトリを取得する
    string GetProjectDirectory(string projectPath);
}
