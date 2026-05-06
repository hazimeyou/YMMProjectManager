namespace YMMProjectManager.Infrastructure.Diff;

public sealed class YmmDiffMatchStatistics
{
    public int OldItemCount { get; set; }
    public int NewItemCount { get; set; }
    public int MatchedByInternalId { get; set; }
    public int MatchedByFallback { get; set; }
    public int UnmatchedOldItems { get; set; }
    public int UnmatchedNewItems { get; set; }
    public int AddedCount { get; set; }
    public int RemovedCount { get; set; }
    public int MovedCount { get; set; }
    public int ModifiedCount { get; set; }
}
