namespace YMMProjectManager.Domain;

//CSJ,record=ProjectMetadataAssociation,プロジェクトとメタデータの関連付け
public sealed record ProjectMetadataAssociation(
    string Directory,
    Guid RootRevisionId,
    string RootSha256,
    Guid SelectedRevisionId,
    string SelectedSha256,
    Guid ObservedHeadRevisionId,
    string ObservedHeadSha256);
