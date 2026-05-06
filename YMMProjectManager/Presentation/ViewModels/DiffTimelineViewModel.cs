using System.Collections.ObjectModel;
using System.Windows.Media;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed class DiffTimelineViewModel : ViewModelBase
{
    private const double Scale = 0.1;
    private const double RowHeight = 28;
    private const double MinimumWidth = 8;
    private const double ItemPadding = 4;

    private DiffTimelineItemViewModel? selectedItem;

    public ObservableCollection<DiffTimelineItemViewModel> Items { get; } = [];

    public DiffTimelineItemViewModel? SelectedItem
    {
        get => selectedItem;
        set => SetProperty(ref selectedItem, value);
    }

    public void SetItems(IEnumerable<DiffTimelineItemViewModel> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
    }

    public DiffTimelineItemViewModel CreateItem(
        string id,
        string kind,
        string category,
        string displayName,
        int timelineIndex,
        int layer,
        int frame,
        int length,
        string? oldValue,
        string? newValue)
    {
        return new DiffTimelineItemViewModel
        {
            Id = id,
            Kind = kind,
            Category = category,
            DisplayName = displayName,
            TimelineIndex = timelineIndex,
            Layer = layer,
            Frame = frame,
            Length = length,
            OldValue = oldValue,
            NewValue = newValue,
            X = frame * Scale,
            Y = Math.Max(0, layer) * RowHeight,
            Width = Math.Max(length * Scale, MinimumWidth),
            Height = RowHeight - ItemPadding,
            Fill = ResolveBrush(kind),
        };
    }

    private static Brush ResolveBrush(string kind)
    {
        return kind switch
        {
            "Added" => Brushes.ForestGreen,
            "Removed" => Brushes.IndianRed,
            "Moved" => Brushes.DarkOrange,
            "Changed" => Brushes.DodgerBlue,
            _ => Brushes.Gray,
        };
    }
}
