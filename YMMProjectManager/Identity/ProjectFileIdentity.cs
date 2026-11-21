namespace YMMProjectManager.Domain;

public sealed record ProjectFileIdentity(ulong Volume, Guid FileId, string Sha256)
{
    public bool IsSameFile(ProjectFileIdentity other) => Volume == other.Volume && FileId == other.FileId;
}
