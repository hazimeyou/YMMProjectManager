using System;

namespace YMMProjectManager.Infrastructure.Checkpoint;

public sealed class CheckpointLogger
{
    private readonly FileLogger logger;

    public CheckpointLogger(FileLogger logger)
    {
        this.logger = logger;
    }

    public void CreationStarted(string projectPath, string checkpointId, string name)
        => logger.Info($"CheckpointCreateStarted projectPath={projectPath}, checkpointId={checkpointId}, name={name}");

    public void CreationSucceeded(string projectPath, string checkpointId)
        => logger.Info($"CheckpointCreateSucceeded projectPath={projectPath}, checkpointId={checkpointId}");

    public void CreationFailed(Exception ex, string projectPath, string checkpointId)
        => logger.Error(ex, $"CheckpointCreateFailed projectPath={projectPath}, checkpointId={checkpointId}");

    public void RestoreStarted(string projectPath, string checkpointId, string outputDirectory)
        => logger.Info($"CheckpointRestoreStarted projectPath={projectPath}, checkpointId={checkpointId}, outputDirectory={outputDirectory}");

    public void RestoreSucceeded(string projectPath, string checkpointId, string restoredProjectPath)
        => logger.Info($"CheckpointRestoreSucceeded projectPath={projectPath}, checkpointId={checkpointId}, restoredProjectPath={restoredProjectPath}");

    public void RestoreFailed(Exception ex, string projectPath, string checkpointId)
        => logger.Error(ex, $"CheckpointRestoreFailed projectPath={projectPath}, checkpointId={checkpointId}");
}
