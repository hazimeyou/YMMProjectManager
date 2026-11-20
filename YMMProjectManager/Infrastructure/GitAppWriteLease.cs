namespace YMMProjectManager.Infrastructure.Git;

/// <summary>Serializes Git commands that can mutate a repository within the plugin process.</summary>
internal sealed class GitAppWriteLease : IDisposable
{
    // One gate also covers paths that refer to the same Git directory through different worktrees.
    private static readonly SemaphoreSlim Writer = new(1, 1);
    private int disposed;

    private GitAppWriteLease() { }

    internal static GitAppWriteLease? TryAcquire(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        return Writer.Wait(0) ? new GitAppWriteLease() : null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) Writer.Release();
    }
}
