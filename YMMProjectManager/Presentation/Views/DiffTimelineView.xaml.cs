using System.Windows;
using System.Windows.Controls;
using YMMProjectManager.Presentation.ViewModels;

namespace YMMProjectManager.Presentation.Views;

public partial class DiffTimelineView : UserControl
{
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
        if (DataContext is not DiffTimelineViewModel vm)
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

    private void OnScrollToSelectedRequested(DiffTimelineItemViewModel item)
    {
        ScrollTo(item);
    }

    private void ScrollTo(DiffTimelineItemViewModel item)
    {
        var targetX = Math.Max(0, item.X - (TimelineScrollViewer.ViewportWidth / 2) + (item.Width / 2));
        var targetY = Math.Max(0, item.Y - (TimelineScrollViewer.ViewportHeight / 2) + (item.Height / 2));
        TimelineScrollViewer.ScrollToHorizontalOffset(targetX);
        TimelineScrollViewer.ScrollToVerticalOffset(targetY);
    }
}
