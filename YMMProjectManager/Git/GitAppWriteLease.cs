namespace YMMProjectManager.Infrastructure.Git;

/// <summary>Serializes Git commands that can mutate a repository within the plugin process.</summary>
//CSJ,class=GitAppWriteLease,アプリ内Git書き込みリース
internal sealed class GitAppWriteLease : IDisposable
{
    // One gate also covers paths that refer to the same Git directory through different worktrees.
    //CSJ,field=Writer,書き込み処理
    private static readonly SemaphoreSlim Writer = new(1, 1);
    //CSJ,field=disposed,破棄済み
    private int disposed;

    private GitAppWriteLease() { }

    //CSJ,method=TryAcquire,取得を試みる
    internal static GitAppWriteLease? TryAcquire(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        return Writer.Wait(0) ? new GitAppWriteLease() : null;
    }

    //CSJ,method=Dispose,破棄する
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) Writer.Release();
    }
}
