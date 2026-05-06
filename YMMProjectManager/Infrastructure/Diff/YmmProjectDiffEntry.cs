namespace YMMProjectManager.Infrastructure.Diff;

public sealed class YmmProjectDiffEntry
{
    public YmmProjectDiffKind Kind { get; set; }
    public string Field { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string? Before { get; set; }
    public string? After { get; set; }
}
