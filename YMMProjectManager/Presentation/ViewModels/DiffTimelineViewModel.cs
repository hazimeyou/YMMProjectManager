using System.Collections.ObjectModel;
using System.Windows.Media;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed class DiffTimelineViewModel : ViewModelBase
{
    private const double MinimumWidth = 8;
    private const double ItemPadding = 4;

    private readonly List<DiffTimelineItemViewModel> allItems = [];
    private readonly ObservableCollection<TimelineRulerMark> rulerMarks = [];
    private DiffTimelineItemViewModel? selectedDiffItem;
    private double scale = 0.1;
    private double rowHeight = 28;
    private int visibleStartFrame;
    private int visibleEndFrame = 1000;
    private int visibleMinLayer;
    private int visibleMaxLayer = 50;
    private double canvasWidth = 2400;
    private double canvasHeight = 1200;
    private int lastVisibleCount;
    private int currentFrame;
    private TimelineSyncState syncState = TimelineSyncState.Unavailable;
    private TimelineMode mode = TimelineMode.Standalone;

    public ObservableCollection<DiffTimelineItemViewModel> VisibleItems { get; } = [];
    public ReadOnlyObservableCollection<TimelineRulerMark> RulerMarks { get; }

    public double MinScale { get; } = 0.02;
    public double MaxScale { get; } = 5.0;

    public double Scale
    {
        get => scale;
        set
        {
            var clamped = Math.Max(MinScale, Math.Min(MaxScale, value));
            if (SetProperty(ref scale, clamped))
            {
                OnPropertyChanged(nameof(CurrentFrameX));
                ReprojectAndFilter(keepSelectionVisible: true);
            }
        }
    }

    public double RowHeight
    {
        get => rowHeight;
        set
        {
            if (SetProperty(ref rowHeight, value <= 4 ? 28 : value))
            {
                ReprojectAndFilter(keepSelectionVisible: true);
            }
        }
    }

    public int VisibleStartFrame
    {
        get => visibleStartFrame;
        set
        {
            if (SetProperty(ref visibleStartFrame, Math.Max(0, value)))
            {
                FilterVisibleItems();
                RebuildRulerMarks();
            }
        }
    }

    public int VisibleEndFrame
    {
        get => visibleEndFrame;
        set
        {
            if (SetProperty(ref visibleEndFrame, Math.Max(value, VisibleStartFrame)))
            {
                FilterVisibleItems();
                RebuildRulerMarks();
            }
        }
    }

    public int VisibleMinLayer
    {
        get => visibleMinLayer;
        set
        {
            if (SetProperty(ref visibleMinLayer, Math.Max(0, value)))
            {
                FilterVisibleItems();
            }
        }
    }

    public int VisibleMaxLayer
    {
        get => visibleMaxLayer;
        set
        {
            if (SetProperty(ref visibleMaxLayer, Math.Max(value, VisibleMinLayer)))
            {
                FilterVisibleItems();
            }
        }
    }

    public double CanvasWidth
    {
        get => canvasWidth;
        private set => SetProperty(ref canvasWidth, value);
    }

    public double CanvasHeight
    {
        get => canvasHeight;
        private set => SetProperty(ref canvasHeight, value);
    }

    public int LastVisibleCount
    {
        get => lastVisibleCount;
        private set => SetProperty(ref lastVisibleCount, value);
    }

    public int CurrentFrame
    {
        get => currentFrame;
        private set
        {
            if (SetProperty(ref currentFrame, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(CurrentFrameX));
            }
        }
    }

    public double CurrentFrameX => CurrentFrame * Scale;

    public TimelineSyncState SyncState
    {
        get => syncState;
        private set => SetProperty(ref syncState, value);
    }

    public TimelineMode Mode
    {
        get => mode;
        private set => SetProperty(ref mode, value);
    }

    public DiffTimelineItemViewModel? SelectedDiffItem
    {
        get => selectedDiffItem;
        set
        {
            if (!SetProperty(ref selectedDiffItem, value))
            {
                return;
            }

            foreach (var item in allItems)
            {
                item.IsSelected = ReferenceEquals(item, value);
            }

            if (value is not null)
            {
                SelectedDiffItemChanged?.Invoke(value);
                ScrollToSelectedRequested?.Invoke(value);
            }
        }
    }

    public event Action<DiffTimelineItemViewModel?>? SelectedDiffItemChanged;
    public event Action<DiffTimelineItemViewModel>? ScrollToSelectedRequested;

    public DiffTimelineViewModel()
    {
        RulerMarks = new ReadOnlyObservableCollection<TimelineRulerMark>(rulerMarks);
    }

    public void SetSyncMode(TimelineMode mode, TimelineSyncState state)
    {
        Mode = mode;
        SyncState = state;
    }

    public void SetCurrentFrame(int frame)
    {
        CurrentFrame = frame;
    }

    public void ZoomIn()
    {
        Scale *= 1.25;
    }

    public void ZoomOut()
    {
        Scale /= 1.25;
    }

    public void ResetZoom()
    {
        Scale = 0.1;
    }

    public void SetItems(IEnumerable<DiffTimelineItemViewModel> items)
    {
        allItems.Clear();
        allItems.AddRange(items);
        ReprojectAndFilter(keepSelectionVisible: false);
    }

    public IReadOnlyList<DiffTimelineItemViewModel> GetVisibleItemsSnapshot()
    {
        return VisibleItems.ToList();
    }

    public void UpdateVisibleFrameRange(int start, int end)
    {
        var normalizedStart = Math.Max(0, Math.Min(start, end));
        var normalizedEnd = Math.Max(normalizedStart, Math.Max(start, end));
        VisibleStartFrame = normalizedStart;
        VisibleEndFrame = normalizedEnd;
    }

    public void UpdateVisibleLayerRange(int minLayer, int maxLayer)
    {
        var normalizedMin = Math.Max(0, Math.Min(minLayer, maxLayer));
        var normalizedMax = Math.Max(normalizedMin, Math.Max(minLayer, maxLayer));
        VisibleMinLayer = normalizedMin;
        VisibleMaxLayer = normalizedMax;
    }

    public void SelectNextDiff()
    {
        if (VisibleItems.Count == 0)
        {
            return;
        }

        if (SelectedDiffItem is null)
        {
            SelectedDiffItem = VisibleItems[0];
            return;
        }

        var index = VisibleItems.IndexOf(SelectedDiffItem);
        SelectedDiffItem = index < 0 || index == VisibleItems.Count - 1 ? VisibleItems[0] : VisibleItems[index + 1];
    }

    public void SelectPreviousDiff()
    {
        if (VisibleItems.Count == 0)
        {
            return;
        }

        if (SelectedDiffItem is null)
        {
            SelectedDiffItem = VisibleItems[^1];
            return;
        }

        var index = VisibleItems.IndexOf(SelectedDiffItem);
        SelectedDiffItem = index <= 0 ? VisibleItems[^1] : VisibleItems[index - 1];
    }

    public bool SelectById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var item = allItems.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
        if (item is null)
        {
            return false;
        }

        SelectedDiffItem = item;
        return true;
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
        var model = new DiffTimelineItemViewModel
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
            Fill = ResolveBrush(kind),
        };

        ProjectItem(model);
        return model;
    }

    private void ReprojectAndFilter(bool keepSelectionVisible)
    {
        foreach (var item in allItems)
        {
            ProjectItem(item);
        }

        CanvasWidth = Math.Max(2400, allItems.Count == 0 ? 2400 : allItems.Max(x => x.X + x.Width + 24));
        CanvasHeight = Math.Max(1200, allItems.Count == 0 ? 1200 : allItems.Max(x => x.Y + x.Height + 24));
        FilterVisibleItems();
        RebuildRulerMarks();

        if (keepSelectionVisible && SelectedDiffItem is not null)
        {
            ScrollToSelectedRequested?.Invoke(SelectedDiffItem);
        }
    }

    private void ProjectItem(DiffTimelineItemViewModel item)
    {
        item.X = item.Frame * Scale;
        item.Y = Math.Max(0, item.Layer) * RowHeight;
        item.Width = Math.Max(item.Length * Scale, MinimumWidth);
        item.Height = RowHeight - ItemPadding;
    }

    private void FilterVisibleItems()
    {
        VisibleItems.Clear();

        foreach (var item in allItems)
        {
            var itemStart = item.Frame;
            var itemEnd = item.Frame + Math.Max(1, item.Length);
            var frameVisible = itemEnd >= VisibleStartFrame && itemStart <= VisibleEndFrame;
            var layerVisible = item.Layer >= VisibleMinLayer && item.Layer <= VisibleMaxLayer;
            if (frameVisible && layerVisible)
            {
                VisibleItems.Add(item);
            }
        }

        LastVisibleCount = VisibleItems.Count;

        if (SelectedDiffItem is not null && !VisibleItems.Contains(SelectedDiffItem))
        {
            SelectedDiffItem = VisibleItems.FirstOrDefault();
        }
    }

    private void RebuildRulerMarks()
    {
        rulerMarks.Clear();
        var step = ResolveRulerStep();
        if (step <= 0)
        {
            step = 300;
        }

        var start = (VisibleStartFrame / step) * step;
        for (var frame = start; frame <= VisibleEndFrame + step; frame += step)
        {
            rulerMarks.Add(new TimelineRulerMark
            {
                Frame = frame,
                X = frame * Scale,
                Label = frame.ToString(),
            });
        }
    }

    private int ResolveRulerStep()
    {
        if (Scale <= 0.05)
        {
            return 1000;
        }

        if (Scale <= 0.2)
        {
            return 300;
        }

        return 30;
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
