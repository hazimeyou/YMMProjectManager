using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Checkpoint;
using YMMProjectManager.Infrastructure.Output;

namespace YMMProjectManager.Presentation.Checkpoint;

public partial class CheckpointDetailWindow : Window, INotifyPropertyChanged
{
    private readonly FileLogger logger;
    private readonly CheckpointService checkpointService;
    private readonly string projectPath;
    private readonly string checkpointId;
    private ThumbnailItem? selectedThumbnailItem;
    private string checkpointName = string.Empty;
    private string summaryText = string.Empty;
    private string ymmpPath = string.Empty;
    private string ymmpxPath = string.Empty;
    private string commentText = string.Empty;
    private string checkpointDirectory = string.Empty;

    public CheckpointDetailWindow(string projectPath, string checkpointId, FileLogger logger)
    {
        InitializeComponent();
        this.projectPath = projectPath;
        this.checkpointId = checkpointId;
        this.logger = logger;
        checkpointService = new CheckpointService(logger);
        DataContext = this;
        Loaded += OnLoaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ThumbnailItem> ThumbnailItems { get; } = [];

    public string CheckpointName
    {
        get => checkpointName;
        private set => SetProperty(ref checkpointName, value, nameof(CheckpointName));
    }

    public string SummaryText
    {
        get => summaryText;
        private set => SetProperty(ref summaryText, value, nameof(SummaryText));
    }

    public string YmmpPath
    {
        get => ymmpPath;
        private set => SetProperty(ref ymmpPath, value, nameof(YmmpPath));
    }

    public string YmmpxPath
    {
        get => ymmpxPath;
        private set => SetProperty(ref ymmpxPath, value, nameof(YmmpxPath));
    }

    public string CommentText
    {
        get => commentText;
        private set => SetProperty(ref commentText, value, nameof(CommentText));
    }

    public ThumbnailItem? SelectedThumbnailItem
    {
        get => selectedThumbnailItem;
        set
        {
            if (!ReferenceEquals(selectedThumbnailItem, value))
            {
                selectedThumbnailItem = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedThumbnailItem)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedThumbnail)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelectedThumbnailVisibility)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MissingSelectedThumbnailVisibility)));
            }
        }
    }

    public ImageSource? SelectedThumbnail => SelectedThumbnailItem?.Image;
    public Visibility HasSelectedThumbnailVisibility => SelectedThumbnail is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MissingSelectedThumbnailVisibility => SelectedThumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async void OnDiagnoseClick(object sender, RoutedEventArgs e)
    {
        var window = new CheckpointDiagnosticsWindow(projectPath, checkpointId, logger)
        {
            Owner = this,
        };
        window.ShowDialog();
        await Task.CompletedTask;
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var message = $"次のチェックポイントを削除します。\n\n名前: {CheckpointName}\n保存先: {checkpointDirectory}\n\n元プロジェクトは削除しません。";
        var answer = MessageBox.Show(message, "チェックポイント削除確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        var result = await checkpointService.DeleteAsync(projectPath, checkpointId);
        MessageBox.Show(
            result.Success ? "チェックポイントを削除しました。" : result.ErrorMessage ?? "チェックポイントの削除に失敗しました。",
            "チェックポイント",
            MessageBoxButton.OK,
            result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);

        if (result.Success)
        {
            DialogResult = true;
            Close();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task LoadAsync()
    {
        var record = await checkpointService.GetCheckpointAsync(projectPath, checkpointId);
        if (record is null)
        {
            MessageBox.Show("チェックポイントを読み込めませんでした。", "チェックポイント", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        checkpointDirectory = record.CheckpointDirectory;
        CheckpointName = record.Name;
        SummaryText = $"作成日時: {record.CreatedAt:yyyy-MM-dd HH:mm:ss}\n説明: {record.Description ?? "-"}\nGit: {record.GitBranch ?? "-"} / {record.GitCommit ?? "-"}\nサムネイル生成方式: {record.ThumbnailMode}";
        YmmpPath = $"ymmp: {record.YmmpPath}";
        YmmpxPath = $"ymmpx: {record.YmmpxPath}";
        CommentText = $"コメント: {record.Comment ?? "-"}";

        ThumbnailItems.Clear();
        foreach (var path in record.ThumbnailPaths)
        {
            var image = await ThumbnailImageLoader.LoadAsync(path, logger);
            ThumbnailItems.Add(new ThumbnailItem
            {
                Path = path,
                DisplayText = System.IO.Path.GetFileName(path),
                Image = image,
            });
        }

        SelectedThumbnailItem = ThumbnailItems.FirstOrDefault();
    }

    private void SetProperty<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed class ThumbnailItem
    {
        public string Path { get; set; } = string.Empty;
        public string DisplayText { get; set; } = string.Empty;
        public ImageSource? Image { get; set; }
        public Visibility HasImageVisibility => Image is not null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility MissingImageVisibility => Image is null ? Visibility.Visible : Visibility.Collapsed;
    }
}
