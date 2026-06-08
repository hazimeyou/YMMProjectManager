using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using YMMProjectManager.Application;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Generations;

namespace YMMProjectManager.Presentation.Generation;

public partial class ProjectGenerationDiagnosticsWindow : Window, INotifyPropertyChanged
{
    private readonly FileLogger logger;
    private readonly IProjectGenerationService generationService;
    private readonly string projectPath;
    private ProjectGenerationDiagnostics diagnostics = new();

    public ProjectGenerationDiagnosticsWindow(string projectPath, FileLogger logger)
    {
        InitializeComponent();
        this.projectPath = projectPath;
        this.logger = logger;
        generationService = new ProjectGenerationService(logger);
        DataContext = this;
        Loaded += OnLoaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ProjectId => diagnostics.ProjectId;
    public string ProjectPath => diagnostics.ProjectPath;
    public int GenerationCount => diagnostics.GenerationCount;
    public string StorageSizeText => $"{diagnostics.StorageSize:N0} B";
    public string LatestGenerationText => diagnostics.LatestGenerationDisplayName is null
        ? "-"
        : $"{diagnostics.LatestGenerationDisplayName} ({diagnostics.LatestGeneration})";
    public string ManifestStatus => diagnostics.ManifestStatus.ToString();
    public int DeletedGenerationCount => diagnostics.DeletedGenerationCount;
    public string LatestGenerationCreatedAtText => diagnostics.LatestGenerationCreatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            logger.Info($"GenerationDiagnosticsOpened projectPath={projectPath}");
            diagnostics = await generationService.GetDiagnosticsAsync(projectPath);
            OnPropertyChanged(nameof(ProjectId));
            OnPropertyChanged(nameof(ProjectPath));
            OnPropertyChanged(nameof(GenerationCount));
            OnPropertyChanged(nameof(StorageSizeText));
            OnPropertyChanged(nameof(LatestGenerationText));
            OnPropertyChanged(nameof(ManifestStatus));
            OnPropertyChanged(nameof(DeletedGenerationCount));
            OnPropertyChanged(nameof(LatestGenerationCreatedAtText));
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"GenerationDiagnosticsOpened failed. projectPath={projectPath}");
            MessageBox.Show("世代診断の取得に失敗しました。ログを確認してください。", "世代診断", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
