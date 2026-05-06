namespace YMMProjectManager.Infrastructure.Ymm;

public sealed class InternalItemIdentity
{
    public string InternalId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int TimelineIndex { get; set; }
    public int Layer { get; set; }
    public int Frame { get; set; }
    public int Length { get; set; }
    public string? Text { get; set; }
    public string? FilePath { get; set; }
}
