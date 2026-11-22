using System;

namespace YMMProjectManager.Domain;

//CSJ,class=ProjectGenerationDiagnostics,プロジェクト世代診断情報
public sealed class ProjectGenerationDiagnostics
{
    //CSJ,property=ProjectId,プロジェクトID
    public string ProjectId { get; set; } = string.Empty;
    //CSJ,property=ProjectPath,プロジェクトのパス
    public string ProjectPath { get; set; } = string.Empty;
    //CSJ,property=GenerationCount,世代件数
    public int GenerationCount { get; set; }
    //CSJ,property=StorageSize,保管サイズ
    public long StorageSize { get; set; }
    //CSJ,property=LatestGeneration,最新世代
    public string? LatestGeneration { get; set; }
    //CSJ,property=ManifestStatus,目録状態
    public ProjectGenerationManifestStatus ManifestStatus { get; set; }
    //CSJ,property=DeletedGenerationCount,削除済み世代件数
    public int DeletedGenerationCount { get; set; }
    //CSJ,property=LatestGenerationCreatedAt,最新世代の作成日時
    public DateTimeOffset? LatestGenerationCreatedAt { get; set; }
    //CSJ,property=LatestGenerationDisplayName,最新世代表示名
    public string? LatestGenerationDisplayName { get; set; }
}
