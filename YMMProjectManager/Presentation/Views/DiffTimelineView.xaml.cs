namespace YMMProjectManager.Presentation.Views;

public partial class DiffTimelineView : UserControl
{
    private bool suppressScrollUpdate;
    private long lastHoverUpdateTicks;
    private int lastHoverFrame = -1;
    private const long HoverUpdateIntervalTicks = TimeSpan.TicksPerMillisecond * 16;

    public DiffTimelineView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is DiffTimelineViewModel oldVm)
        {
            oldVm.ScrollToSelectedRequested -= OnScrollToSelectedRequested;
        }

        if (e.NewValue is DiffTimelineViewModel newVm)
        {
            newVm.ScrollToSelectedRequested += OnScrollToSelectedRequested;
        }
    }

    private void OnTimelineItemClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DiffTimelineViewModel vm)
        {
            return;
        }

        if (sender is FrameworkElement fe && fe.DataContext is DiffTimelineItemViewModel item)
        {
            vm.SelectedDiffItem = item;
        }
    }

    private void OnTimelineScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (suppressScrollUpdate || DataContext is not DiffTimelineViewModel vm)
        {
            return;
        }

        var startFrame = (int)(TimelineScrollViewer.HorizontalOffset / vm.Scale);
        var endFrame = (int)((TimelineScrollViewer.HorizontalOffset + TimelineScrollViewer.ViewportWidth) / vm.Scale);
        var minLayer = (int)(TimelineScrollViewer.VerticalOffset / vm.RowHeight);
        var maxLayer = (int)((TimelineScrollViewer.VerticalOffset + TimelineScrollViewer.ViewportHeight) / vm.RowHeight);

        vm.UpdateVisibleFrameRange(startFrame, endFrame);
        vm.UpdateVisibleLayerRange(minLayer, maxLayer);
    }

    private void OnPrevDiffClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DiffTimelineViewModel vm)
        {
            vm.SelectPreviousDiff();
        }
    }

    private void OnNextDiffClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DiffTimelineViewModel vm)
        {
            vm.SelectNextDiff();
        }
    }

    private void OnScrollToSelectedClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DiffTimelineViewModel vm && vm.SelectedDiffItem is not null)
        {
            ScrollTo(vm.SelectedDiffItem);
        }
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DiffTimelineViewModel vm)
        {
            vm.ZoomIn();
        }
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DiffTimelineViewModel vm)
        {
            vm.ZoomOut();
        }
    }

    private void OnResetZoomClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DiffTimelineViewModel vm)
        {
            vm.ResetZoom();
        }
    }

    private void OnZoomPreset25Click(object sender, RoutedEventArgs e) => SetZoomPreset(0.25);
    private void OnZoomPreset50Click(object sender, RoutedEventArgs e) => SetZoomPreset(0.5);
    private void OnZoomPreset100Click(object sender, RoutedEventArgs e) => SetZoomPreset(1.0);
    private void OnZoomPreset200Click(object sender, RoutedEventArgs e) => SetZoomPreset(2.0);

    private void SetZoomPreset(double value)
    {
        if (DataContext is DiffTimelineViewModel vm)
        {
            vm.Scale = value;
        }
    }

    private void OnFitTimelineClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DiffTimelineViewModel vm || TimelineScrollViewer.ViewportWidth <= 0)
        {
            return;
        }

        var frameSpan = Math.Max(120, vm.MaxFrame - vm.MinFrame);
        var rawScale = TimelineScrollViewer.ViewportWidth / frameSpan;
        var targetScale = Math.Max(0.05, Math.Min(2.5, rawScale));
        vm.Scale = targetScale;
        vm.UpdateVisibleFrameRange(vm.MinFrame, vm.MaxFrame);
    }

    private void OnTimelinePreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not DiffTimelineViewModel vm)
        {
            return;
        }

        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        if (e.Delta > 0)
        {
            vm.ZoomIn();
        }
        else
        {
            vm.ZoomOut();
        }

        e.Handled = true;
    }

    private void OnScrollToSelectedRequested(DiffTimelineItemViewModel item)
    {
        ScrollTo(item);
    }

    private void ScrollTo(DiffTimelineItemViewModel item)
    {
        // Only auto-scroll when selection is outside the current viewport.
        var itemLeft = item.X;
        var itemRight = item.X + item.Width;
        var itemTop = item.Y;
        var itemBottom = item.Y + item.Height;
        var viewportLeft = TimelineScrollViewer.HorizontalOffset;
        var viewportRight = viewportLeft + TimelineScrollViewer.ViewportWidth;
        var viewportTop = TimelineScrollViewer.VerticalOffset;
        var viewportBottom = viewportTop + TimelineScrollViewer.ViewportHeight;
        var isHorizontallyVisible = itemRight >= viewportLeft && itemLeft <= viewportRight;
        var isVerticallyVisible = itemBottom >= viewportTop && itemTop <= viewportBottom;
        if (isHorizontallyVisible && isVerticallyVisible)
        {
            return;
        }

        suppressScrollUpdate = true;
        try
        {
            var targetX = Math.Max(0, item.X - (TimelineScrollViewer.ViewportWidth / 2) + (item.Width / 2));
            var targetY = Math.Max(0, item.Y - (TimelineScrollViewer.ViewportHeight / 2) + (item.Height / 2));
            TimelineScrollViewer.ScrollToHorizontalOffset(targetX);
            TimelineScrollViewer.ScrollToVerticalOffset(targetY);
        }
        finally
        {
            suppressScrollUpdate = false;
        }

        if (DataContext is DiffTimelineViewModel vm)
        {
            var startFrame = (int)(TimelineScrollViewer.HorizontalOffset / vm.Scale);
            var endFrame = (int)((TimelineScrollViewer.HorizontalOffset + TimelineScrollViewer.ViewportWidth) / vm.Scale);
            var minLayer = (int)(TimelineScrollViewer.VerticalOffset / vm.RowHeight);
            var maxLayer = (int)((TimelineScrollViewer.VerticalOffset + TimelineScrollViewer.ViewportHeight) / vm.RowHeight);
            vm.UpdateVisibleFrameRange(startFrame, endFrame);
            vm.UpdateVisibleLayerRange(minLayer, maxLayer);
        }
    }

    private void OnTimelineMouseMove(object sender, MouseEventArgs e)
    {
        if (DataContext is not DiffTimelineViewModel vm || vm.Scale <= 0)
        {
            return;
        }

        var now = DateTime.UtcNow.Ticks;
        if (now - lastHoverUpdateTicks < HoverUpdateIntervalTicks)
        {
            return;
        }

        var pos = e.GetPosition(TimelineScrollViewer);
        var frame = Math.Max(0, (int)((TimelineScrollViewer.HorizontalOffset + pos.X) / vm.Scale));
        if (frame == lastHoverFrame)
        {
            return;
        }

        lastHoverUpdateTicks = now;
        lastHoverFrame = frame;
        var x = Math.Max(0, TimelineScrollViewer.HorizontalOffset + pos.X);

        HoverFrameLine.X1 = x;
        HoverFrameLine.X2 = x;
        HoverFrameLine.Visibility = Visibility.Visible;

        HoverFrameText.Text = $"F: {frame}";
        HoverFrameBadge.Margin = new Thickness(Math.Min(x + 8, Math.Max(0, vm.CanvasWidth - 80)), 4, 0, 0);
        HoverFrameBadge.Visibility = Visibility.Visible;
    }

    private void OnTimelineMouseLeave(object sender, MouseEventArgs e)
    {
        lastHoverFrame = -1;
        HoverFrameLine.Visibility = Visibility.Collapsed;
        HoverFrameBadge.Visibility = Visibility.Collapsed;
    }
}
