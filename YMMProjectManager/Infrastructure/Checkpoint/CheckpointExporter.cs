using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure.Packaging;

namespace YMMProjectManager.Infrastructure.Checkpoint;

public sealed class CheckpointExporter
{
    private readonly CheckpointStorage storage;
    private readonly YmmpxLibBundleService bundleService;

    public CheckpointExporter(CheckpointStorage storage, YmmpxLibBundleService bundleService)
    {
        this.storage = storage;
        this.bundleService = bundleService;
    }

    public async Task ExportAsync(string projectPath, string checkpointId, CancellationToken cancellationToken = default)
    {
        await storage.CopyFileAsync(projectPath, storage.GetYmmpPath(projectPath, checkpointId), cancellationToken).ConfigureAwait(false);
        var ymmpxPath = storage.GetYmmpxPath(projectPath, checkpointId);
        var result = await bundleService.CreatePackageAsync(projectPath, ymmpxPath, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.ErrorMessage ?? "ymmpx の作成に失敗しました。");
        }
    }
}
