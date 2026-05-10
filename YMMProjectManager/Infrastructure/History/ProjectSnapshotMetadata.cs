namespace YMMProjectManager.Infrastructure.History;

public sealed class ProjectSnapshotMetadata
{
    public string SnapshotId { get; set; } = string.Empty;
    public string ProjectPath { get; set; } = string.Empty;
    public string ProjectKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public long OriginalFileSize { get; set; }
    public int InternalItemIdVersion { get; set; } = 1;
}
