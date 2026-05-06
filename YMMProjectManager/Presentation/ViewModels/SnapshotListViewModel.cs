
namespace YMMProjectManager.Presentation.ViewModels;

public sealed class SnapshotListViewModel : ViewModelBase
{
    private readonly ProjectSnapshotService snapshotService;
    private readonly string projectPath;
    private ProjectSnapshotMetadata? selectedSnapshot;

    public ObservableCollection<ProjectSnapshotMetadata> Snapshots { get; } = [];

    public ProjectSnapshotMetadata? SelectedSnapshot
    {
        get => selectedSnapshot;
        set => SetProperty(ref selectedSnapshot, value);
    }

    public SnapshotListViewModel(ProjectSnapshotService snapshotService, string projectPath)
    {
        this.snapshotService = snapshotService;
        this.projectPath = projectPath;
    }

    public async Task ReloadAsync()
    {
        var list = await snapshotService.GetSnapshotsAsync(projectPath).ConfigureAwait(true);
        Snapshots.Clear();
        foreach (var x in list)
        {
            Snapshots.Add(x);
        }
    }

    public async Task<bool> DeleteSelectedAsync()
    {
        if (SelectedSnapshot is null)
        {
            return false;
        }

        var ok = await snapshotService.DeleteSnapshotAsync(projectPath, SelectedSnapshot.SnapshotId).ConfigureAwait(true);
        if (ok)
        {
            await ReloadAsync().ConfigureAwait(true);
        }

        return ok;
    }
}
