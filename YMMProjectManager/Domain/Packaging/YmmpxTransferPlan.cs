namespace YMMProjectManager.Domain.Packaging;

public enum YmmpxTransferPermission
{
    Unconfirmed,
    Prohibited,
    RedistributionAllowed,
    OwnWork,
}

public sealed record YmmpxTransferCandidate(
    string SourcePath, string OriginalFileName, long? Length, string? Sha256, string? ExclusionReason)
{
    public bool CanInclude => ExclusionReason is null && Length.HasValue && Sha256 is not null;
}

public sealed class YmmpxTransferPlan
{
    internal YmmpxTransferPlan(string projectPath, string projectSha256, IReadOnlyList<YmmpxTransferCandidate> assets)
    {
        ProjectPath = projectPath;
        ProjectSha256 = projectSha256;
        Assets = assets;
    }

    public string ProjectPath { get; }
    public IReadOnlyList<YmmpxTransferCandidate> Assets { get; }
    internal string ProjectSha256 { get; }
}
