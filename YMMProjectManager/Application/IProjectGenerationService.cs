using YMMProjectManager.Domain;

namespace YMMProjectManager.Application;

public interface IProjectGenerationService
{
    Task<ProjectGenerationRecord> CreateGenerationAsync(string projectPath, string displayName, string? memo, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectGenerationRecord>> GetGenerationsAsync(string projectPath, CancellationToken cancellationToken = default);
    Task<ProjectGenerationDiagnostics> GetDiagnosticsAsync(string projectPath, CancellationToken cancellationToken = default);
    Task<(bool Success, string? ErrorMessage, ProjectGenerationRecord? Generation)> RestoreGenerationAsync(string projectPath, string generationId, GenerationRestoreMode restoreMode, CancellationToken cancellationToken = default);
    Task<(bool Success, string? ErrorMessage)> DeleteGenerationAsync(string projectPath, string generationId, CancellationToken cancellationToken = default);
    string GetRootDirectory();
    string GetProjectDirectory(string projectPath);
}
