namespace YMMProjectManager.Domain;

//CSJ,record=ProjectFileIdentity,プロジェクトファイルの識別情報
public sealed record ProjectFileIdentity(ulong Volume, Guid FileId, string Sha256)
{
    //CSJ,method=IsSameFile,同一ファイルかを判定する
    public bool IsSameFile(ProjectFileIdentity other) => Volume == other.Volume && FileId == other.FileId;
}
