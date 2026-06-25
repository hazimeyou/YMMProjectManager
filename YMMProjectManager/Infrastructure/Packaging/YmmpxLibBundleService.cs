using YMMProjectManager.Domain.Packaging;

namespace YMMProjectManager.Infrastructure.Packaging;

public sealed class YmmpxLibBundleService
{
    private readonly YmmpxLibBridge bridge;

    public YmmpxLibBundleService(FileLogger logger, string? explicitAssemblyPath = null, string? baseDirectory = null)
    {
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
        return bridge.CreatePackageAsync(ymmpPath, outputYmmpxPath, token, progress);
    }

    public Task<YmmpxExtractResult> ExtractPackageAsync(
        string ymmpxPath,
        string outputDirectory,
        CancellationToken token,
        IProgress<double>? progress = null)
    {
        return bridge.ExtractPackageAsync(ymmpxPath, outputDirectory, token, progress);
    }
}
