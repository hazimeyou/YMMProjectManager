namespace YMMProjectManager.Infrastructure.Git;

//CSJ,class=GitCloneDirectory,Gitクローンディレクトリ
internal sealed class GitCloneDirectory(OwnedStagingDirectory directory) : IDisposable
{
    //CSJ,property=Path,パス
    internal string Path => directory.Path;

    internal static GitCloneDirectory Reserve(string parent) =>
        new(OwnedStagingDirectory.Reserve(parent, ".ymmpm-clone-"));

    //CSJ,method=Publish,確定反映する
    internal void Publish(string destination, CancellationToken cancellationToken) =>
        directory.Publish(destination, cancellationToken);

    //CSJ,method=CheckRecoveryPath,復旧パスを確認する
    internal string? CheckRecoveryPath() => directory.CheckRecoveryPath();

    //CSJ,method=Dispose,破棄する
    public void Dispose() => directory.Dispose();
}
