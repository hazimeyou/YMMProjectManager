using YMMProjectManager.Domain;

namespace YMMProjectManager.Application;

public interface ICheckpointService
{
    Task<CheckpointRecord> CreateAsync(CheckpointCreateRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CheckpointRecord>> GetCheckpointsAsync(string projectPath, CancellationToken cancellationToken = default);
    Task<CheckpointRecord?> GetCheckpointAsync(string projectPath, string checkpointId, CancellationToken cancellationToken = default);
    Task<CheckpointRestoreResult> RestoreAsync(CheckpointRestoreRequest request, CancellationToken cancellationToken = default);
}
