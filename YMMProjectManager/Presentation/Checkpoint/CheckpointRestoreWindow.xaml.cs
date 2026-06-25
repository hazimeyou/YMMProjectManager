using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Checkpoint;

namespace YMMProjectManager.Presentation.Checkpoint;

public partial class CheckpointRestoreWindow : Window, INotifyPropertyChanged
{
    private readonly string projectPath;
    private readonly string checkpointId;
    private readonly CheckpointService checkpointService;
    private string outputDirectory = string.Empty;
    private string statusMessage = string.Empty;

    public CheckpointRestoreWindow(string projectPath, string checkpointId, FileLogger logger)
    {
        InitializeComponent();
        this.projectPath = projectPath;
        this.checkpointId = checkpointId;
        checkpointService = new CheckpointService(logger);
        outputDirectory = Path.Combine(Path.GetDirectoryName(projectPath) ?? AppContext.BaseDirectory, $"{Path.GetFileNameWithoutExtension(projectPath)}-restored");
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string OutputDirectory
    {
        get => outputDirectory;
        set => SetField(ref outputDirectory, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        set => SetField(ref statusMessage, value);
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "復元先フォルダーを選択",
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            OutputDirectory = dialog.FolderName;
        }
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        var result = await checkpointService.RestoreAsync(new CheckpointRestoreRequest
        {
            ProjectPath = projectPath,
            CheckpointId = checkpointId,
            OutputDirectory = OutputDirectory,
        });

        StatusMessage = result.Success
            ? $"復元しました: {result.RestoredProjectPath}"
            : result.ErrorMessage ?? "復元に失敗しました。";

        MessageBox.Show(StatusMessage, "チェックポイント復元", MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
