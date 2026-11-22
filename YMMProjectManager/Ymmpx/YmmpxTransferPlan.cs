namespace YMMProjectManager.Domain.Packaging;

//CSJ,enum=YmmpxTransferPermission,YMMPX受け渡しへの素材同梱許可
public enum YmmpxTransferPermission
{
    Unconfirmed,
    Prohibited,
    RedistributionAllowed,
    OwnWork,
}

//CSJ,record=YmmpxTransferCandidate,YMMPX受け渡し候補
public sealed record YmmpxTransferCandidate(
    string SourcePath, string OriginalFileName, long? Length, string? Sha256, string? ExclusionReason)
{
    //CSJ,property=CanInclude,同梱の可否
    public bool CanInclude => ExclusionReason is null && Length.HasValue && Sha256 is not null;
}

//CSJ,class=YmmpxTransferPlan,YMMPX受け渡し計画
public sealed class YmmpxTransferPlan
{
    internal YmmpxTransferPlan(string projectPath, string projectSha256, IReadOnlyList<YmmpxTransferCandidate> assets)
    {
        ProjectPath = projectPath;
        ProjectSha256 = projectSha256;
        Assets = assets;
    }

    //CSJ,property=ProjectPath,プロジェクトのパス
    public string ProjectPath { get; }
    //CSJ,property=Assets,素材一覧
    public IReadOnlyList<YmmpxTransferCandidate> Assets { get; }
    //CSJ,property=ProjectSha256,プロジェクトSHA-256
    internal string ProjectSha256 { get; }
}
