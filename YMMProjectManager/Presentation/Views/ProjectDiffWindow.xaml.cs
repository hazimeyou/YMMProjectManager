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
    private void OnOpenExperimentalHostClick(object sender, RoutedEventArgs e) => Vm?.OpenExperimentalDiagnosticsHost();
    private void OnClearFiltersClick(object sender, RoutedEventArgs e) => Vm?.ClearStandaloneFilters();
    private void OnExpandAllGroupsClick(object sender, RoutedEventArgs e) => Vm?.ExpandAllGroups();
    private void OnCollapseAllGroupsClick(object sender, RoutedEventArgs e) => Vm?.CollapseAllGroups();
    private void OnSetOldSnapshotClick(object sender, RoutedEventArgs e) => Vm?.SelectSnapshotAsOld();
    private void OnSetNewSnapshotClick(object sender, RoutedEventArgs e) => Vm?.SelectSnapshotAsNew();
    private void OnSwapSnapshotSelectionClick(object sender, RoutedEventArgs e) => Vm?.SwapSnapshotSelection();
    private void OnClearSnapshotSelectionClick(object sender, RoutedEventArgs e) => Vm?.ClearSnapshotSelection();
    private void OnRunSnapshotCompareClick(object sender, RoutedEventArgs e) => Vm?.RunSelectedSnapshotCompare();

    private void OnClosed(object? sender, EventArgs e)
    {
        Vm?.Dispose();
        Closed -= OnClosed;
    }
}
