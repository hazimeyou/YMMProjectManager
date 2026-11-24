using System.Windows;

namespace YMMProjectManager.Presentation.Views;

public partial class ProjectListView
{
    private void OnCompareProjectsClick(object sender, RoutedEventArgs e)
    {
        var window = new ProjectComparisonWindow();
        if (Window.GetWindow(this) is { } owner) window.Owner = owner;
        window.ShowDialog();
    }
}
