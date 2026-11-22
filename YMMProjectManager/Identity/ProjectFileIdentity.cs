namespace YMMProjectManager.Domain;

public sealed record ProjectFileIdentity(ulong Volume, Guid FileId, string Sha256)
{
    //CSJ,method=IsSameFile,同一ファイルかを判定する
    public bool IsSameFile(ProjectFileIdentity other) => Volume == other.Volume && FileId == other.FileId;
}
