namespace YMMProjectManager.Presentation.ViewModels;

public sealed class TimelineRulerMark
{
    public int Frame { get; set; }
    public double X { get; set; }
    public string Label { get; set; } = string.Empty;
}
