using YMMProjectManager.Presentation.TimelinePresentation.Display;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed class DiffTimelineViewModel : ViewModelBase
{
    private const double MinimumWidth = 8;
    private const double ItemPadding = 4;

    private readonly List<DiffTimelineItemViewModel> allItems = [];
    private readonly ObservableCollection<TimelineRulerMark> rulerMarks = [];
    private readonly ObservableCollection<LayerRowMark> layerRows = [];
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
    private bool showUnchangedItems = true;

    public ObservableCollection<DiffTimelineItemViewModel> VisibleItems { get; } = [];
    public ReadOnlyObservableCollection<TimelineRulerMark> RulerMarks { get; }
    public ReadOnlyObservableCollection<LayerRowMark> LayerRows { get; }

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
    public bool HasVisibleItems => LastVisibleCount > 0;
    public string EmptyTimelineMessage => "この履歴には表示できるタイムラインアイテムがありません";
    public int ItemCount => allItems.Count;
    public int LayerCount => allItems.Count == 0 ? 0 : allItems.Max(x => Math.Max(0, x.Layer)) + 1;
    public int MinFrame => allItems.Count == 0 ? 0 : allItems.Min(x => Math.Max(0, x.Frame));
    public int MaxFrame => allItems.Count == 0 ? 0 : allItems.Max(x => Math.Max(0, x.Frame + Math.Max(1, x.Length)));
    public bool TimeGuideExtendsFullHeight => true;

    public TimelineSyncState SyncState
    {
        get => syncState;
        private set
        {
            if (SetProperty(ref syncState, value))
            {
                OnPropertyChanged(nameof(SyncStateLabel));
            }
        }
    }

    public TimelineMode Mode
    {
        get => mode;
        private set
        {
            if (SetProperty(ref mode, value))
            {
                OnPropertyChanged(nameof(ModeLabel));
            }
        }
    }

    public string SyncStateLabel => syncState switch
    {
        TimelineSyncState.Unavailable => "利用不可",
        TimelineSyncState.Detached => "切断",
        TimelineSyncState.Synced => "同期中",
        TimelineSyncState.Manual => "手動",
        TimelineSyncState.Error => "エラー",
        _ => syncState.ToString(),
    };

    public string ModeLabel => mode switch
    {
        TimelineMode.Standalone => "単独",
        TimelineMode.Synced => "同期",
        TimelineMode.Comparison => "比較",
        _ => mode.ToString(),
    };

    public bool ShowUnchangedItems
    {
        get => showUnchangedItems;
        set
        {
            if (SetProperty(ref showUnchangedItems, value))
            {
                FilterVisibleItems();
            }
        }
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
    public event Action<int, bool>? ScrollToFrameRequested;

    public DiffTimelineViewModel()
    {
        RulerMarks = new ReadOnlyObservableCollection<TimelineRulerMark>(rulerMarks);
        LayerRows = new ReadOnlyObservableCollection<LayerRowMark>(layerRows);
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

    public void ScrollToCurrentFrame()
    {
        ScrollToFrameRequested?.Invoke(CurrentFrame, false);
    }

    public void CenterCurrentFrame()
    {
        ScrollToFrameRequested?.Invoke(CurrentFrame, true);
    }

    public bool SelectNearestDiffToCurrentFrame()
    {
        if (allItems.Count == 0)
        {
            return false;
        }

        var nearest = allItems
            .OrderBy(x => Math.Abs(x.Frame - CurrentFrame))
            .ThenBy(x => x.Frame)
            .FirstOrDefault();
        if (nearest is null)
        {
            return false;
        }

        SelectedDiffItem = nearest;
        return true;
    }

    public bool JumpToFirstDiff()
    {
        var first = allItems.OrderBy(x => x.Frame).ThenBy(x => x.Layer).FirstOrDefault();
        if (first is null) return false;
        SelectedDiffItem = first;
        SetCurrentFrame(first.Frame);
        return true;
    }

    public bool JumpToLastDiff()
    {
        var last = allItems.OrderByDescending(x => x.Frame).ThenByDescending(x => x.Layer).FirstOrDefault();
        if (last is null) return false;
        SelectedDiffItem = last;
        SetCurrentFrame(last.Frame);
        return true;
    }

    public bool JumpToPreviousDiffFromCurrentFrame()
    {
        var prev = allItems
            .Where(x => x.Frame < CurrentFrame)
            .OrderByDescending(x => x.Frame)
            .ThenByDescending(x => x.Layer)
            .FirstOrDefault();
        if (prev is null) return false;
        SelectedDiffItem = prev;
        SetCurrentFrame(prev.Frame);
        return true;
    }

    public bool JumpToNextDiffFromCurrentFrame()
    {
        var next = allItems
            .Where(x => x.Frame > CurrentFrame)
            .OrderBy(x => x.Frame)
            .ThenBy(x => x.Layer)
            .FirstOrDefault();
        if (next is null) return false;
        SelectedDiffItem = next;
        SetCurrentFrame(next.Frame);
        return true;
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
        var clipTypeLabel = DiffTimelineClipDisplayResolver.ResolveClipTypeLabel(displayName, category, oldValue, newValue);
        var isUnchanged = IsUnchangedKind(kind);
        var model = new DiffTimelineItemViewModel
        {
            Id = id,
            Kind = kind,
            Category = category,
            DisplayName = displayName,
            ClipTypeLabel = clipTypeLabel,
            ClipTitle = DiffTimelineClipDisplayResolver.BuildClipTitle(displayName),
            TimelineIndex = timelineIndex,
            Layer = layer,
            Frame = frame,
            Length = length,
            LayerHint = $"L{Math.Max(0, layer)}",
            DurationHint = $"{Math.Max(1, length)}f",
            IsUnchanged = isUnchanged,
            ItemOpacity = isUnchanged ? 0.33 : 0.92,
            BorderOpacity = isUnchanged ? 0.16 : 0.36,
            OldValue = oldValue,
            NewValue = newValue,
            Fill = ResolveBrush(kind, clipTypeLabel),
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
        RebuildLayerRows();
        OnPropertyChanged(nameof(ItemCount));
        OnPropertyChanged(nameof(LayerCount));
        OnPropertyChanged(nameof(MinFrame));
        OnPropertyChanged(nameof(MaxFrame));

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
            if (frameVisible && layerVisible && (ShowUnchangedItems || !item.IsUnchanged))
            {
                VisibleItems.Add(item);
            }
        }

        LastVisibleCount = VisibleItems.Count;
        OnPropertyChanged(nameof(HasVisibleItems));

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


    private void RebuildLayerRows()
    {
        layerRows.Clear();
        if (LayerCount <= 0)
        {
            return;
        }

        for (var layer = 0; layer < LayerCount; layer++)
        {
            layerRows.Add(new LayerRowMark
            {
                Layer = layer,
                Y = layer * RowHeight,
                Label = $"Layer {layer}",
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


    private static bool IsUnchangedKind(string kind)
    {
        return string.Equals(kind, "一致", StringComparison.Ordinal) ||
               string.Equals(kind, "Unchanged", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(kind, "Same", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(kind, "None", StringComparison.OrdinalIgnoreCase);
    }

    private static Brush ResolveBrush(string kind, string clipTypeLabel)
    {
        // Prefer clip semantic coloring (YMM-like subdued tones), then fallback to diff-kind color.
        var clipBrush = clipTypeLabel switch
        {
            "テキスト" => new SolidColorBrush(Color.FromRgb(76, 124, 168)),
            "図形" => new SolidColorBrush(Color.FromRgb(150, 88, 88)),
            "画像" => new SolidColorBrush(Color.FromRgb(116, 98, 154)),
            "音声" => new SolidColorBrush(Color.FromRgb(82, 138, 108)),
            "動画" => new SolidColorBrush(Color.FromRgb(166, 120, 72)),
            _ => null,
        };
        if (clipBrush is not null)
        {
            clipBrush.Freeze();
            return clipBrush;
        }

        var kindBrush = kind switch
        {
            "Added" or "追加" => new SolidColorBrush(Color.FromRgb(82, 138, 108)),
            "Removed" or "削除" => new SolidColorBrush(Color.FromRgb(150, 88, 88)),
            "Moved" or "移動" => new SolidColorBrush(Color.FromRgb(166, 120, 72)),
            "Changed" or "変更" => new SolidColorBrush(Color.FromRgb(76, 124, 168)),
            _ => new SolidColorBrush(Color.FromRgb(108, 108, 108)),
        };
        kindBrush.Freeze();
        return kindBrush;
    }

}






public sealed class LayerRowMark
{
    public int Layer { get; init; }
    public double Y { get; init; }
    public string Label { get; init; } = string.Empty;
}
