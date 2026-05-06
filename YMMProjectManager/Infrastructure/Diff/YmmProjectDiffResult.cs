namespace YMMProjectManager.Infrastructure.Diff;

public sealed class YmmProjectDiffResult
{
    public IReadOnlyList<YmmProjectDiffEntry> Entries { get; set; } = [];
    public YmmDiffMatchStatistics Statistics { get; set; } = new();
}
