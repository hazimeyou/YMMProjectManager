namespace YMMProjectManager.Domain.Packaging;

// Serialized by legacy PM transfer manifests only.
internal enum YmmpxTransferPermission
{
    Unconfirmed,
    Prohibited,
    RedistributionAllowed,
    OwnWork,
}

internal enum YmmpxMaterialDisposition
{
    Included,
    Excluded,
    Unavailable,
}

internal sealed record YmmpxTransferCandidate(string SourcePath, string OriginalFileName, long? Length,
    string? Sha256, YmmpxMaterialDisposition Disposition, string? Reason, bool SequenceMember = false);

internal sealed record YmmpxTransferPlan(string ProjectPath, string ProjectSha256,
    IReadOnlyList<YmmpxTransferCandidate> Assets, (ulong, Guid) ProjectIdentity);
