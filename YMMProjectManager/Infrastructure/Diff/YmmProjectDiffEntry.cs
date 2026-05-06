namespace YMMProjectManager.Infrastructure.Diff;

public sealed class YmmProjectDiffEntry
{
    public YmmProjectDiffKind Kind { get; set; }
    public string Field { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string Category { get; set; } = "TimelineItem";
    public int TimelineIndex { get; set; }
    public int Layer { get; set; }
    public int Frame { get; set; }
    public int Length { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
}
