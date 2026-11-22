using System;
using System.Collections.Generic;

namespace YMMProjectManager.Domain;

//CSJ,class=ProjectGenerationManifest,プロジェクト世代目録
public sealed class ProjectGenerationManifest
{
    public const int CurrentSchemaVersion = 1;

    //CSJ,property=SchemaVersion,スキーマのバージョン
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    //CSJ,property=ProjectId,プロジェクトID
    public string ProjectId { get; set; } = string.Empty;
    //CSJ,property=ProjectPath,プロジェクトのパス
    public string ProjectPath { get; set; } = string.Empty;
    //CSJ,property=ProjectFileName,プロジェクトファイル名
    public string ProjectFileName { get; set; } = string.Empty;
    //CSJ,property=CreatedAt,作成日時
    public DateTimeOffset CreatedAt { get; set; }
    //CSJ,property=UpdatedAt,更新日時
    public DateTimeOffset UpdatedAt { get; set; }
    //CSJ,property=Generations,世代
    public List<ProjectGenerationManifestItem> Generations { get; set; } = [];
}
