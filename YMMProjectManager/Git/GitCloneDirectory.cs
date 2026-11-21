namespace YMMProjectManager.Infrastructure.Git;

internal sealed class GitCloneDirectory(OwnedStagingDirectory directory) : IDisposable
{
    internal string Path => directory.Path;

    internal static GitCloneDirectory Reserve(string parent) =>
        new(OwnedStagingDirectory.Reserve(parent, ".ymmpm-clone-"));

    internal void Publish(string destination, CancellationToken cancellationToken) =>
        directory.Publish(destination, cancellationToken);

    internal string? CheckRecoveryPath() => directory.CheckRecoveryPath();

    public void Dispose() => directory.Dispose();
}
