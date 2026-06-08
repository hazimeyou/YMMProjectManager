using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using YMMProjectManager.Application;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Generations;

namespace YMMProjectManager.Presentation.Generation;

public partial class ProjectGenerationListWindow : Window
{
    private readonly FileLogger logger;
    private readonly IProjectGenerationService generationService;
    private readonly string projectPath;

    public ProjectGenerationListWindow(string projectPath, FileLogger logger)
    {
        InitializeComponent();
        this.projectPath = projectPath;
        this.logger = logger;
        generationService = new ProjectGenerationService(logger);
        DataContext = this;
        Loaded += OnLoaded;
    }

    public ObservableCollection<ProjectGenerationListRow> Generations { get; } = [];
    public ProjectGenerationListRow? SelectedGeneration { get; set; }
    public string ProjectDisplayText => $"対象プロジェクト: {projectPath}";
    public string StoragePathText => $"保存先: {generationService.GetProjectDirectory(projectPath)}";

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (SelectedGeneration is null)
        {
            return;
        }

        var result = MessageBox.Show(
            "選択した世代を元のプロジェクトへ復元します。",
            "世代一覧",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        var (success, errorMessage, _) = await generationService.RestoreGenerationAsync(projectPath, SelectedGeneration.GenerationId, GenerationRestoreMode.RestoreToOriginalWithBackup);
        if (!success)
        {
            MessageBox.Show(errorMessage ?? "復元に失敗しました。", "世代一覧", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show("復元しました。", "世代一覧", MessageBoxButton.OK, MessageBoxImage.Information);
        await RefreshAsync();
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (SelectedGeneration is null)
        {
            return;
        }

        var result = MessageBox.Show(
            "選択した世代を削除します。削除済みフォルダへ移動します。",
            "世代一覧",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        var (success, errorMessage) = await generationService.DeleteGenerationAsync(projectPath, SelectedGeneration.GenerationId);
        if (!success)
        {
            MessageBox.Show(errorMessage ?? "削除に失敗しました。", "世代一覧", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show("削除しました。", "世代一覧", MessageBoxButton.OK, MessageBoxImage.Information);
        await RefreshAsync();
    }

    private void OnOpenStorageClick(object sender, RoutedEventArgs e)
    {
        var path = generationService.GetProjectDirectory(projectPath);
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    private async Task RefreshAsync()
    {
        try
        {
            logger.Info($"GenerationListRefresh start. projectPath={projectPath}");
            var generations = await generationService.GetGenerationsAsync(projectPath);
            Generations.Clear();
            foreach (var generation in generations)
            {
                Generations.Add(ProjectGenerationListRow.FromRecord(generation));
            }

            logger.Info($"GenerationListRefresh end. projectPath={projectPath}, count={Generations.Count}");
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"GenerationListRefresh failed. projectPath={projectPath}");
            MessageBox.Show("世代一覧の更新に失敗しました。ログを確認してください。", "世代一覧", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public sealed class ProjectGenerationListRow
    {
        public string GenerationId { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public string CreatedAtString => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
        public string DisplayName { get; set; } = string.Empty;
        public string? Memo { get; set; }
        public string FileSizeText { get; set; } = string.Empty;
        public string SourceProjectPath { get; set; } = string.Empty;
        public string StatusText { get; set; } = string.Empty;

        public static ProjectGenerationListRow FromRecord(ProjectGenerationRecord record)
        {
            return new ProjectGenerationListRow
            {
                GenerationId = record.GenerationId,
                CreatedAt = record.CreatedAt,
                DisplayName = record.DisplayName,
                Memo = record.Memo,
                FileSizeText = $"{record.FileSize:N0} B",
                SourceProjectPath = record.SourceProjectPath,
                StatusText = record.IsValid ? "正常" : $"異常: {record.IssueMessage}",
            };
        }
    }
}
