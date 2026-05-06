using System.Windows;

namespace YMMProjectManager.Presentation.Views;

public partial class ProjectDiffWindow : Window
{
    public ProjectDiffWindow(ProjectDiffViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closed += OnClosed;
    }

    private ProjectDiffViewModel? Vm => DataContext as ProjectDiffViewModel;

    private void OnSyncFrameClick(object sender, RoutedEventArgs e) => Vm?.SyncFrameFromPlaceholder();
    private void OnGoToCurrentFrameClick(object sender, RoutedEventArgs e) => Vm?.GoToCurrentFrame();
    private void OnCenterCurrentFrameClick(object sender, RoutedEventArgs e) => Vm?.CenterCurrentFrame();
    private void OnNearestDiffClick(object sender, RoutedEventArgs e) => Vm?.SelectNearestDiffToCurrentFrame();
    private void OnFirstDiffClick(object sender, RoutedEventArgs e) => Vm?.JumpToFirstDiff();
    private void OnLastDiffClick(object sender, RoutedEventArgs e) => Vm?.JumpToLastDiff();
    private void OnPrevFromFrameClick(object sender, RoutedEventArgs e) => Vm?.JumpToPreviousDiffFromCurrentFrame();
    private void OnNextFromFrameClick(object sender, RoutedEventArgs e) => Vm?.JumpToNextDiffFromCurrentFrame();

    private void OnClosed(object? sender, EventArgs e)
    {
        Vm?.Dispose();
        Closed -= OnClosed;
    }
}
