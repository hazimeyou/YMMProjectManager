namespace YMMProjectManager.Infrastructure.Ymm;

public sealed class YmmItemModel
{
    public string Scope { get; set; } = string.Empty;
    public string? Type { get; set; }
    public int TimelineIndex { get; set; } = -1;
    public int Layer { get; set; }
    public int Frame { get; set; }
    public int Length { get; set; }
    public string? Text { get; set; }
    public string? FilePath { get; set; }
    public string? InternalId { get; set; }
    public Dictionary<string, string?> Fields { get; } = new(StringComparer.Ordinal);
}
