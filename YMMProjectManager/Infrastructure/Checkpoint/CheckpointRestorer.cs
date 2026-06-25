using System.IO;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure.Packaging;

namespace YMMProjectManager.Infrastructure.Checkpoint;

public sealed class CheckpointRestorer
{
    private readonly CheckpointStorage storage;
    private readonly YmmpxLibBundleService bundleService;

    public CheckpointRestorer(CheckpointStorage storage, YmmpxLibBundleService bundleService)
    {
        this.storage = storage;
        this.bundleService = bundleService;
    }

    public async Task<CheckpointRestoreResult> RestoreAsync(string projectPath, string checkpointId, string outputDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);

        var ymmpxPath = storage.GetYmmpxPath(projectPath, checkpointId);
        if (!File.Exists(ymmpxPath))
        {
            return new CheckpointRestoreResult
            {
                Success = false,
                ErrorMessage = "復元元の ymmpx が見つかりません。",
            };
        }

        var result = await bundleService.ExtractPackageAsync(ymmpxPath, outputDirectory, cancellationToken).ConfigureAwait(false);
        return new CheckpointRestoreResult
        {
            Success = result.Success,
            ErrorMessage = result.ErrorMessage,
            RestoredProjectPath = result.RestoredProjectPath,
            RestoredYmmpxPath = ymmpxPath,
        };
    }
}
