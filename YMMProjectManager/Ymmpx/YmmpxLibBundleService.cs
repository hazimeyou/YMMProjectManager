using YMMProjectManager.Domain.Packaging;

namespace YMMProjectManager.Infrastructure.Packaging;

//CSJ,class=YmmpxLibBundleService,YMMPXライブラリ一括パッケージサービス
public sealed class YmmpxLibBundleService
{
    private readonly YmmpxLibBridge bridge;
    //CSJ,field=logger,ロガー
    private readonly FileLogger logger;

    public YmmpxLibBundleService(FileLogger logger, string? explicitAssemblyPath = null, string? baseDirectory = null)
    {
        this.logger = logger;
        bridge = new YmmpxLibBridge(logger, explicitAssemblyPath, baseDirectory);
    }

    //CSJ,property=MissingLibraryMessage,欠落ライブラリメッセージ
    public string? MissingLibraryMessage => bridge.MissingLibraryMessage;

    //CSJ,property=SearchedPaths,検索済みのパス一覧
    public IReadOnlyList<string> SearchedPaths => bridge.SearchedPaths;

    //CSJ,method=TryEnsureAvailable,利用可能の確認と準備を試みる
    public bool TryEnsureAvailable(out string? resolvedAssemblyPath)
    {
        return bridge.TryEnsureAvailable(out resolvedAssemblyPath);
    }

    //CSJ,method=CreatePackageAsync,パッケージを作成する（非同期）
    public Task<YmmpxPackageResult> CreatePackageAsync(
        //CSJ,parameter=ymmpPath,YMMPパス
        string ymmpPath,
        //CSJ,parameter=outputYmmpxPath,出力YMMPXパス
        string outputYmmpxPath,
        CancellationToken token,
        //CSJ,parameter=progress,進捗
        IProgress<double>? progress = null)
    {
        return Task.Run(() => bridge.CreatePackageAsync(ymmpPath, outputYmmpxPath, token, progress), token);
    }

    //CSJ,method=ExtractPackageAsync,パッケージを抽出する（非同期）
    public Task<YmmpxExtractResult> ExtractPackageAsync(
        //CSJ,parameter=ymmpxPath,YMMPXパス
        string ymmpxPath,
        //CSJ,parameter=outputDirectory,出力ディレクトリ
        string outputDirectory,
        CancellationToken token,
        //CSJ,parameter=progress,進捗
        IProgress<double>? progress = null)
    {
        return bridge.ExtractPackageAsync(ymmpxPath, outputDirectory, token, progress);
    }

    //CSJ,method=InspectTransferAsync,受け渡しを点検する（非同期）
    public Task<YmmpxTransferPlan> InspectTransferAsync(
        //CSJ,parameter=projectPath,プロジェクトのパス
        string projectPath,
        CancellationToken token,
        IReadOnlyList<string>? allowedMaterialRoots = null)
    {
        return Task.Run(() => YmmpxTransfer.InspectAsync(projectPath, token, allowedMaterialRoots), token);
    }

    //CSJ,method=CreateTransferAsync,受け渡しを作成する（非同期）
    public Task<YmmpxPackageResult> CreateTransferAsync(
        //CSJ,parameter=plan,計画
        YmmpxTransferPlan plan,
        IReadOnlyDictionary<string, YmmpxTransferPermission> approvals,
        //CSJ,parameter=newOutputDirectory,新規出力ディレクトリ
        string newOutputDirectory,
        CancellationToken token,
        //CSJ,parameter=progress,進捗
        IProgress<double>? progress = null)
    {
        return Task.Run(() => YmmpxTransfer.CreateAsync(plan, approvals, newOutputDirectory, token, progress, logger), token);
    }

    //CSJ,method=ExtractTransferAsync,受け渡しを抽出する（非同期）
    public Task<YmmpxExtractResult> ExtractTransferAsync(
        //CSJ,parameter=packagePath,パッケージのパス
        string packagePath,
        //CSJ,parameter=outputDirectory,出力ディレクトリ
        string outputDirectory,
        CancellationToken token,
        //CSJ,parameter=progress,進捗
        IProgress<double>? progress = null)
    {
        return bridge.ExtractPackageAsync(packagePath, outputDirectory, token, progress, requireTransfer: true);
    }
}
