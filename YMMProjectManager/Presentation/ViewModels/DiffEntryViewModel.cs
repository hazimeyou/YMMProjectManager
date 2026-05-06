namespace YMMProjectManager.Presentation.ViewModels;

public sealed class DiffEntryViewModel
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public string Before { get; set; } = string.Empty;
    public string After { get; set; } = string.Empty;
    public int TimelineIndex { get; set; }
    public int Layer { get; set; }
    public int Frame { get; set; }
    public int Length { get; set; }
}
