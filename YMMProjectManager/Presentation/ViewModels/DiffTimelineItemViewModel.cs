
namespace YMMProjectManager.Presentation.ViewModels;

public sealed class DiffTimelineItemViewModel
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Category { get; set; } = "TimelineItem";
    public string DisplayName { get; set; } = string.Empty;
    public int TimelineIndex { get; set; }
    public int Layer { get; set; }
    public int Frame { get; set; }
    public int Length { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public Brush Fill { get; set; } = Brushes.Gray;
    public bool IsSelected { get; set; }
}
