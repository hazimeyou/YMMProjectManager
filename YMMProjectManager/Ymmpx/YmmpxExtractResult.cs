namespace YMMProjectManager.Domain.Packaging;

//CSJ,class=YmmpxExtractResult,YMMPX抽出結果
public sealed class YmmpxExtractResult
{
    //CSJ,property=Success,成功
    public bool Success { get; init; }

    //CSJ,property=ErrorMessage,エラーメッセージ
    public string? ErrorMessage { get; init; }

    //CSJ,property=RestoredProjectPath,復元済みプロジェクトのパス
    public string? RestoredProjectPath { get; init; }
    //CSJ,property=RestoredProjectIdentity,復元済みプロジェクト識別情報
    public ProjectFileIdentity? RestoredProjectIdentity { get; init; }

    //CSJ,property=OutputDirectory,出力ディレクトリ
    public string? OutputDirectory { get; init; }

    //CSJ,property=ReplacedPathCount,置換済みのパス数
    public int ReplacedPathCount { get; init; }

    //CSJ,property=ExtractedResourceCount,抽出したリソース数
    public int ExtractedResourceCount { get; init; }

    //CSJ,property=TransferEvidenceVerified,受け渡し証跡検証済み
    public bool TransferEvidenceVerified { get; init; }
    //CSJ,property=MissingMaterials,欠落素材
    public IReadOnlyList<string> MissingMaterials { get; init; } = [];
    //CSJ,property=Links,リンク一覧
    public IReadOnlyList<YmmpxPackageLink> Links { get; init; } = [];
}
