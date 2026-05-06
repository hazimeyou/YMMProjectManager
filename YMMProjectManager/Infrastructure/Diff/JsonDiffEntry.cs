namespace YMMProjectManager.Infrastructure.Diff;

public sealed class JsonDiffEntry
{
    public JsonDiffKind Kind { get; set; }
    public string Path { get; set; } = string.Empty;
    public string? Before { get; set; }
    public string? After { get; set; }
}
