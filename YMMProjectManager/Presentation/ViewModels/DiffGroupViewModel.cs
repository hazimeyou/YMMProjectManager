namespace YMMProjectManager.Presentation.ViewModels;

public sealed class DiffGroupViewModel
{
    public string GroupName { get; set; } = string.Empty;
    public IReadOnlyList<DiffEntryViewModel> Items { get; set; } = [];
    public int Count { get; set; }
}
