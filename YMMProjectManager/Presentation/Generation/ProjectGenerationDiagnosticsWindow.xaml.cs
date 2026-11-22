using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using YMMProjectManager.Application;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Generations;

namespace YMMProjectManager.Presentation.Generation;

//CSJ,class=ProjectGenerationDiagnosticsWindow,プロジェクト世代診断情報ウィンドウ
public partial class ProjectGenerationDiagnosticsWindow : Window, INotifyPropertyChanged
{
    //CSJ,field=logger,ロガー
    private readonly FileLogger logger;
    //CSJ,field=generationService,世代サービス
    private readonly IProjectGenerationService generationService;
    //CSJ,field=projectPath,プロジェクトのパス
    private readonly string projectPath;
    //CSJ,field=diagnostics,診断情報
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

    //CSJ,event=PropertyChanged,プロパティ変更済み
    public event PropertyChangedEventHandler? PropertyChanged;

    //CSJ,property=ProjectId,プロジェクトID
    public string ProjectId => diagnostics.ProjectId;
    //CSJ,property=ProjectPath,プロジェクトのパス
    public string ProjectPath => diagnostics.ProjectPath;
    //CSJ,property=GenerationCount,世代件数
    public int GenerationCount => diagnostics.GenerationCount;
    //CSJ,property=StorageSizeText,保管サイズテキスト
    public string StorageSizeText => $"{diagnostics.StorageSize:N0} B";
    //CSJ,property=LatestGenerationText,最新世代テキスト
    public string LatestGenerationText => diagnostics.LatestGenerationDisplayName is null
        ? "-"
        : $"{diagnostics.LatestGenerationDisplayName} ({diagnostics.LatestGeneration})";
    //CSJ,property=ManifestStatus,目録状態
    public string ManifestStatus => diagnostics.ManifestStatus.ToString();
    //CSJ,property=DeletedGenerationCount,削除済み世代件数
    public int DeletedGenerationCount => diagnostics.DeletedGenerationCount;
    //CSJ,property=LatestGenerationCreatedAtText,最新世代の作成日時の表示文字列
    public string LatestGenerationCreatedAtText => diagnostics.LatestGenerationCreatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";

    //CSJ,method=OnLoaded,読み込み済み時の処理
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    //CSJ,method=OnRefreshClick,更新クリック時の処理
    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    //CSJ,method=RefreshAsync,更新する（非同期）
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

    //CSJ,method=OnPropertyChanged,プロパティ変更済み時の処理
    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
