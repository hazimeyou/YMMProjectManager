namespace YMMProjectManager.Domain.Packaging;

//CSJ,class=YmmpxPackageResult,YMMPXパッケージ結果
public sealed class YmmpxPackageResult
{
    //CSJ,property=Success,成功
    public bool Success { get; init; }

    //CSJ,property=ErrorMessage,エラーメッセージ
    public string? ErrorMessage { get; init; }

    //CSJ,property=OutputPath,出力パス
    public string? OutputPath { get; init; }

    //CSJ,property=DetectedMaterialCount,検出した素材数
    public int DetectedMaterialCount { get; init; }

    //CSJ,property=PackagedMaterialCount,パッケージに含めた素材数
    public int PackagedMaterialCount { get; init; }

    //CSJ,property=MissingMaterialCount,欠落素材件数
    public int MissingMaterialCount { get; init; }

    //CSJ,property=DuplicateMaterialCount,重複素材件数
    public int DuplicateMaterialCount { get; init; }

    //CSJ,property=Links,リンク一覧
    public IReadOnlyList<YmmpxPackageLink> Links { get; init; } = [];
}

//CSJ,class=YmmpxPackageLink,YMMPXパッケージリンク
public sealed class YmmpxPackageLink
{
    //CSJ,property=OriginalPath,元のパス
    public string OriginalPath { get; init; } = string.Empty;

    //CSJ,property=ResolvedPath,解決済みのパス
    public string? ResolvedPath { get; init; }

    //CSJ,property=BundlePath,一括パッケージのパス
    public string BundlePath { get; init; } = string.Empty;

    //CSJ,property=Exists,存在するか判定する
    public bool Exists { get; init; }

    //CSJ,property=Status,状態
    public string? Status { get; init; }
}
