using YMMProjectManager.Domain;

namespace YMMProjectManager.Infrastructure.Generations;

//CSJ,class=ProjectGenerationManifestLoadResult,プロジェクト世代目録読み込み結果
public sealed class ProjectGenerationManifestLoadResult
{
    public ProjectGenerationManifestLoadResult(ProjectGenerationManifest? manifest, ProjectGenerationManifestStatus status, string? errorMessage = null, string? contentHash = null)
    {
        Manifest = manifest;
        Status = status;
        ErrorMessage = errorMessage;
        ContentHash = contentHash;
    }

    //CSJ,property=Manifest,目録
    public ProjectGenerationManifest? Manifest { get; }
    //CSJ,property=Status,状態
    public ProjectGenerationManifestStatus Status { get; }
    //CSJ,property=ErrorMessage,エラーメッセージ
    public string? ErrorMessage { get; }
    //CSJ,property=ContentHash,内容のハッシュ
    internal string? ContentHash { get; }
}
