using System;

namespace YMMProjectManager.Domain;

//CSJ,class=ProjectGenerationRecord,プロジェクト世代記録
public sealed class ProjectGenerationRecord
{
    //CSJ,property=GenerationId,世代ID
    public string GenerationId { get; set; } = string.Empty;
    //CSJ,property=CreatedAt,作成日時
    public DateTimeOffset CreatedAt { get; set; }
    //CSJ,property=DisplayName,表示名
    public string DisplayName { get; set; } = string.Empty;
    //CSJ,property=Memo,メモ
    public string? Memo { get; set; }
    //CSJ,property=SourceProjectPath,元プロジェクトのパス
    public string SourceProjectPath { get; set; } = string.Empty;
    //CSJ,property=FileSize,ファイルサイズ
    public long FileSize { get; set; }
    //CSJ,property=Sha256,SHA-256ハッシュ
    public string Sha256 { get; set; } = string.Empty;
    //CSJ,property=CreatedByVersion,作成時のバージョン
    public string CreatedByVersion { get; set; } = string.Empty;
    //CSJ,property=GenerationPath,世代パス
    public string GenerationPath { get; set; } = string.Empty;
    //CSJ,property=IsValid,有効かどうか
    public bool IsValid { get; set; } = true;
    //CSJ,property=IssueMessage,イシューメッセージ
    public string? IssueMessage { get; set; }
}
