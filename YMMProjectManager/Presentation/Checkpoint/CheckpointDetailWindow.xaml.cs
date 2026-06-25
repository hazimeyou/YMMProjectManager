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
            }
        }
    }

    public ImageSource? SelectedThumbnail => SelectedThumbnailItem?.Image;

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var record = await checkpointService.GetCheckpointAsync(projectPath, checkpointId);
        if (record is null)
        {
            Close();
            return;
        }

        CheckpointName = record.Name;
        SummaryText = $"作成日時: {record.CreatedAt:yyyy-MM-dd HH:mm:ss}\nGit: {record.GitBranch ?? "-"} / {record.GitCommit ?? "-"}\n生成方式: {record.ThumbnailMode}";
        YmmpPath = $"ymmp: {record.YmmpPath}";
        YmmpxPath = $"ymmpx: {record.YmmpxPath}";
        CommentText = $"コメント: {record.Comment ?? "-"}";

        ThumbnailItems.Clear();
        foreach (var path in record.ThumbnailPaths)
        {
            ThumbnailItems.Add(new ThumbnailItem
            {
                Path = path,
                Image = await ThumbnailImageLoader.LoadAsync(path, logger),
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
        public ImageSource? Image { get; set; }
    }
}
