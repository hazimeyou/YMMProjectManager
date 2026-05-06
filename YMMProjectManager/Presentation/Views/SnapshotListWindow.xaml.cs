using System.Windows;

namespace YMMProjectManager.Presentation.Views;

public partial class SnapshotListWindow : Window
{
    private readonly string projectPath;
    private readonly FileLogger logger;
    private readonly ProjectSnapshotService snapshotService;
    private readonly JsonNormalizeService normalizeService;
    private readonly JsonDiffService jsonDiffService;
    private readonly YmmProjectDiffService ymmDiffService;

    public SnapshotListWindow(
        string projectPath,
        FileLogger logger,
        ProjectSnapshotService snapshotService,
        JsonNormalizeService normalizeService,
        JsonDiffService jsonDiffService,
        YmmProjectDiffService ymmDiffService)
    {
        InitializeComponent();
        this.projectPath = projectPath;
        this.logger = logger;
        this.snapshotService = snapshotService;
        this.normalizeService = normalizeService;
        this.jsonDiffService = jsonDiffService;
        this.ymmDiffService = ymmDiffService;
        DataContext = new SnapshotListViewModel(snapshotService, projectPath);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SnapshotListViewModel vm)
        {
            await vm.ReloadAsync();
        }
    }

    private async void OnReloadClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SnapshotListViewModel vm)
        {
            await vm.ReloadAsync();
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SnapshotListViewModel vm || vm.SelectedSnapshot is null)
        {
            return;
        }

        if (MessageBox.Show("選択スナップショットを削除します。", "確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await vm.DeleteSelectedAsync();
    }

    private async void OnCompareCurrentClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SnapshotListViewModel vm || vm.SelectedSnapshot is null)
        {
            return;
        }

        await OpenDiffForCurrentAsync(vm.SelectedSnapshot.SnapshotId);
    }

    private async void OnCompareSelectedClick(object sender, RoutedEventArgs e)
    {
        var selected = SnapshotGrid.SelectedItems.Cast<ProjectSnapshotMetadata>().ToList();
        if (selected.Count != 2)
        {
            MessageBox.Show("2件選択してください。", "確認 / 比較", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await OpenDiffForSnapshotsAsync(selected[0].SnapshotId, selected[1].SnapshotId);
    }

    private async Task OpenDiffForCurrentAsync(string snapshotId)
    {
        var vm = new ProjectDiffViewModel(logger, snapshotService, normalizeService, jsonDiffService, ymmDiffService);
        await vm.LoadCurrentVsSnapshotDiffAsync(projectPath, snapshotId);
        new ProjectDiffWindow(vm) { Owner = this }.ShowDialog();
    }

    private async Task OpenDiffForSnapshotsAsync(string leftId, string rightId)
    {
        var vm = new ProjectDiffViewModel(logger, snapshotService, normalizeService, jsonDiffService, ymmDiffService);
        await vm.LoadSnapshotsDiffAsync(projectPath, leftId, rightId);
        new ProjectDiffWindow(vm) { Owner = this }.ShowDialog();
    }
}
