using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Checkpoint;
using YMMProjectManager.Infrastructure.Output;

namespace YMMProjectManager.Presentation.Checkpoint;

public partial class CheckpointListWindow : Window, INotifyPropertyChanged
{
    private readonly FileLogger logger;
    private readonly CheckpointService checkpointService;
    private readonly string projectPath;
    private CheckpointListRow? selectedCheckpoint;

    public CheckpointListWindow(string projectPath, FileLogger logger)
    {
        InitializeComponent();
        this.projectPath = projectPath;
        this.logger = logger;
        checkpointService = new CheckpointService(logger);
        DataContext = this;
        Loaded += OnLoaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<CheckpointListRow> Checkpoints { get; } = [];
    public string ProjectDisplayText => $"対象プロジェクト: {projectPath}";
    public string StoragePathText => $"保存先: {checkpointService.GetProjectDirectory(projectPath)}";

    public CheckpointListRow? SelectedCheckpoint
    {
        get => selectedCheckpoint;
        set
        {
            if (!ReferenceEquals(selectedCheckpoint, value))
            {
                selectedCheckpoint = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedCheckpoint)));
            }
        }
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e) => await RefreshAsync();
    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnDetailClick(object sender, RoutedEventArgs e)
    {
        if (SelectedCheckpoint is null)
        {
            return;
        }

        var window = new CheckpointDetailWindow(projectPath, SelectedCheckpoint.CheckpointId, logger)
        {
            Owner = this,
        };
        window.ShowDialog();
        await RefreshAsync();
    }

    private async void OnDiagnoseClick(object sender, RoutedEventArgs e)
    {
        if (SelectedCheckpoint is null)
        {
            return;
        }

        var window = new CheckpointDiagnosticsWindow(projectPath, SelectedCheckpoint.CheckpointId, logger)
        {
            Owner = this,
        };
        window.ShowDialog();
        await Task.CompletedTask;
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (SelectedCheckpoint is null)
        {
            return;
        }

        var window = new CheckpointRestoreWindow(projectPath, SelectedCheckpoint.CheckpointId, logger)
        {
            Owner = this,
        };
        window.ShowDialog();
        await RefreshAsync();
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (SelectedCheckpoint is null)
        {
            return;
        }

        var message = $"次のチェックポイントを削除します。\n\n名前: {SelectedCheckpoint.Name}\n作成日時: {SelectedCheckpoint.CreatedAtText}\n保存先: {SelectedCheckpoint.CheckpointDirectory}\n\n元プロジェクトは削除しません。";
        var answer = MessageBox.Show(message, "チェックポイント削除確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        var result = await checkpointService.DeleteAsync(projectPath, SelectedCheckpoint.CheckpointId);
        MessageBox.Show(
            result.Success ? "チェックポイントを削除しました。" : result.ErrorMessage ?? "チェックポイントの削除に失敗しました。",
            "チェックポイント",
            MessageBoxButton.OK,
            result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);

        await RefreshAsync();
    }

    private void OnOpenStorageClick(object sender, RoutedEventArgs e)
    {
        var path = checkpointService.GetProjectDirectory(projectPath);
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private async Task RefreshAsync()
    {
        var records = await checkpointService.GetCheckpointsAsync(projectPath);
        Checkpoints.Clear();
        foreach (var record in records)
        {
            Checkpoints.Add(await CheckpointListRow.FromRecordAsync(record, logger));
        }

        SelectedCheckpoint = Checkpoints.FirstOrDefault();
    }

    public sealed class CheckpointListRow
    {
        public string CheckpointId { get; set; } = string.Empty;
        public string CreatedAtText { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? GitCommit { get; set; }
        public string? GitBranch { get; set; }
        public string ThumbnailMode { get; set; } = string.Empty;
        public ImageSource? RepresentativeThumbnail { get; set; }
        public bool HasRepresentativeThumbnail => RepresentativeThumbnail is not null;
        public Visibility HasRepresentativeThumbnailVisibility => HasRepresentativeThumbnail ? Visibility.Visible : Visibility.Collapsed;
        public Visibility MissingRepresentativeThumbnailVisibility => HasRepresentativeThumbnail ? Visibility.Collapsed : Visibility.Visible;
        public string StatusText { get; set; } = string.Empty;
        public string CheckpointDirectory { get; set; } = string.Empty;

        public static async Task<CheckpointListRow> FromRecordAsync(CheckpointRecord record, FileLogger logger)
        {
            var thumbnail = string.IsNullOrWhiteSpace(record.RepresentativeThumbnailPath)
                ? null
                : await ThumbnailImageLoader.LoadAsync(record.RepresentativeThumbnailPath, logger);

            return new CheckpointListRow
            {
                CheckpointId = record.CheckpointId,
                CreatedAtText = record.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                Name = record.Name,
                Description = string.IsNullOrWhiteSpace(record.Description) ? "-" : record.Description,
                GitCommit = string.IsNullOrWhiteSpace(record.GitCommit) ? "-" : record.GitCommit,
                GitBranch = string.IsNullOrWhiteSpace(record.GitBranch) ? "-" : record.GitBranch,
                ThumbnailMode = record.ThumbnailMode,
                RepresentativeThumbnail = thumbnail,
                StatusText = record.IsValid ? "利用可" : "要確認",
                CheckpointDirectory = record.CheckpointDirectory,
            };
        }
    }
}
