namespace YMMProjectManager.Domain;

//CSJ,enum=ProjectGenerationManifestStatus,プロジェクト世代目録状態
public enum ProjectGenerationManifestStatus
{
    //CSJ,field=Valid,有効
    Valid = 0,
    //CSJ,field=Missing,欠落
    Missing = 1,
    Corrupted = 2,
    UnsupportedSchema = 3,
}
