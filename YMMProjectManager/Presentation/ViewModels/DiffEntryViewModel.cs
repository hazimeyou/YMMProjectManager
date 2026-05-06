namespace YMMProjectManager.Presentation.ViewModels;

public sealed class DiffEntryViewModel
{
    public string Kind { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public string Before { get; set; } = string.Empty;
    public string After { get; set; } = string.Empty;
}
