using YMMProjectManager.Domain.Packaging;

namespace YMMProjectManager.Infrastructure.Packaging;

public sealed class YmmpxLibBundleService
{
    private readonly YmmpxLibBridge bridge;
    private readonly FileLogger logger;

    public YmmpxLibBundleService(FileLogger logger, string? explicitAssemblyPath = null, string? baseDirectory = null)
    {
        this.logger = logger;
        bridge = new YmmpxLibBridge(logger, explicitAssemblyPath, baseDirectory);
    }

    public string? MissingLibraryMessage => bridge.MissingLibraryMessage;

    public IReadOnlyList<string> SearchedPaths => bridge.SearchedPaths;

    public bool TryEnsureAvailable(out string? resolvedAssemblyPath)
    {
        return bridge.TryEnsureAvailable(out resolvedAssemblyPath);
    }

    public Task<YmmpxPackageResult> CreatePackageAsync(
        string ymmpPath,
        string outputYmmpxPath,
        CancellationToken token,
        IProgress<double>? progress = null)
    {
        return Task.Run(() => bridge.CreatePackageAsync(ymmpPath, outputYmmpxPath, token, progress), token);
    }

    public Task<YmmpxExtractResult> ExtractPackageAsync(
        string ymmpxPath,
        string outputDirectory,
        CancellationToken token,
        IProgress<double>? progress = null)
    {
        return bridge.ExtractPackageAsync(ymmpxPath, outputDirectory, token, progress);
    }

    public Task<YmmpxTransferPlan> InspectTransferAsync(
        string projectPath,
        CancellationToken token,
        IReadOnlyList<string>? allowedMaterialRoots = null)
    {
        return Task.Run(() => YmmpxTransfer.InspectAsync(projectPath, token, allowedMaterialRoots), token);
    }

    public Task<YmmpxPackageResult> CreateTransferAsync(
        YmmpxTransferPlan plan,
        IReadOnlyDictionary<string, YmmpxTransferPermission> approvals,
        string newOutputDirectory,
        CancellationToken token,
        IProgress<double>? progress = null)
    {
        return Task.Run(() => YmmpxTransfer.CreateAsync(plan, approvals, newOutputDirectory, token, progress, logger), token);
    }

    public Task<YmmpxExtractResult> ExtractTransferAsync(
        string packagePath,
        string outputDirectory,
        CancellationToken token,
        IProgress<double>? progress = null)
    {
        return bridge.ExtractPackageAsync(packagePath, outputDirectory, token, progress, requireTransfer: true);
    }
}
