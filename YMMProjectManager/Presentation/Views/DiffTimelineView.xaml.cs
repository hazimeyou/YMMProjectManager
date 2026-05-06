using System.Windows;
using System.Windows.Controls;
using YMMProjectManager.Presentation.ViewModels;

namespace YMMProjectManager.Presentation.Views;

public partial class DiffTimelineView : UserControl
{
    public DiffTimelineView()
    {
        InitializeComponent();
    }

    private void OnTimelineItemClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DiffTimelineViewModel vm)
        {
            return;
        }

        if (sender is FrameworkElement fe && fe.DataContext is DiffTimelineItemViewModel item)
        {
            vm.SelectedItem = item;
        }
    }
}
