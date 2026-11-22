using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using YMMProjectManager.Application;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Generations;

namespace YMMProjectManager.Presentation.Generation;

//CSJ,class=ProjectGenerationListWindow,保存世代の一覧画面
public partial class ProjectGenerationListWindow : Window, INotifyPropertyChanged
{
    //CSJ,field=logger,ロガー
    private readonly FileLogger logger;
    //CSJ,field=generationService,世代サービス
    private readonly IProjectGenerationService generationService;
    //CSJ,field=projectPath,プロジェクトのパス
    private readonly string projectPath;
    //CSJ,field=selectedGeneration,選択中の世代
    private ProjectGenerationListRow? selectedGeneration;
    //CSJ,field=isEmptyStateVisible,空状態表示中かどうか
    private bool isEmptyStateVisible;
    //CSJ,field=emptyStateMessage,空状態メッセージ
    private string emptyStateMessage = "このプロジェクトには保存された世代がありません。";
    //CSJ,field=canRestore,復元の可否
    private bool canRestore;
    //CSJ,field=canDelete,削除の可否
    private bool canDelete;

    public ProjectGenerationListWindow(string projectPath, FileLogger logger)
    {
        InitializeComponent();
        this.projectPath = projectPath;
        this.logger = logger;
        generationService = new ProjectGenerationService(logger);
        DataContext = this;
        Loaded += OnLoaded;
        Generations.CollectionChanged += (_, _) => UpdateState();
        UpdateState();
    }

    //CSJ,property=Generations,世代
    public ObservableCollection<ProjectGenerationListRow> Generations { get; } = [];

    //CSJ,property=SelectedGeneration,選択中の世代
    public ProjectGenerationListRow? SelectedGeneration
    {
        get => selectedGeneration;
        set
        {
            if (!ReferenceEquals(selectedGeneration, value))
            {
                selectedGeneration = value;
                OnPropertyChanged(nameof(SelectedGeneration));
                UpdateState();
            }
        }
    }

    //CSJ,property=ProjectDisplayText,プロジェクト表示テキスト
    public string ProjectDisplayText => $"対象プロジェクト: {projectPath}";
    //CSJ,property=StoragePathText,保管パステキスト
    public string StoragePathText => $"保存先: {generationService.GetProjectDirectory(projectPath)}";

    //CSJ,property=IsEmptyStateVisible,空状態表示中かどうか
    public bool IsEmptyStateVisible
    {
        get => isEmptyStateVisible;
        private set
        {
            if (isEmptyStateVisible != value)
            {
                isEmptyStateVisible = value;
                OnPropertyChanged(nameof(IsEmptyStateVisible));
            }
        }
    }

    //CSJ,property=EmptyStateMessage,空状態メッセージ
    public string EmptyStateMessage
    {
        get => emptyStateMessage;
        private set
        {
            if (emptyStateMessage != value)
            {
                emptyStateMessage = value;
                OnPropertyChanged(nameof(EmptyStateMessage));
            }
        }
    }

    //CSJ,property=CanRestore,復元の可否
    public bool CanRestore
    {
        get => canRestore;
        private set
        {
            if (canRestore != value)
            {
                canRestore = value;
                OnPropertyChanged(nameof(CanRestore));
            }
        }
    }

    //CSJ,property=CanDelete,削除の可否
    public bool CanDelete
    {
        get => canDelete;
        private set
        {
            if (canDelete != value)
            {
                canDelete = value;
                OnPropertyChanged(nameof(CanDelete));
            }
        }
    }

    //CSJ,event=PropertyChanged,プロパティ変更済み
    public event PropertyChangedEventHandler? PropertyChanged;

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

    //CSJ,method=OnRestoreClick,復元クリック時の処理
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

    //CSJ,method=OnDeleteClick,削除クリック時の処理
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

    //CSJ,method=OnOpenStorageClick,表示保管クリック時の処理
    private void OnOpenStorageClick(object sender, RoutedEventArgs e)
    {
        //CSJ,variable=path,パス
        var path = generationService.GetProjectDirectory(projectPath);
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    //CSJ,method=RefreshAsync,更新する（非同期）
    private async Task RefreshAsync()
    {
        try
        {
            logger.Info($"GenerationListRefresh start. projectPath={projectPath}");
            //CSJ,variable=generations,世代
            var generations = await generationService.GetGenerationsAsync(projectPath);
            Generations.Clear();
            //CSJ,variable=generation,世代
            foreach (var generation in generations)
            {
                Generations.Add(ProjectGenerationListRow.FromRecord(generation));
            }

            SelectedGeneration = Generations.FirstOrDefault();
            logger.Info($"GenerationListRefresh end. projectPath={projectPath}, count={Generations.Count}");
            UpdateState();
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"GenerationListRefresh failed. projectPath={projectPath}");
            IsEmptyStateVisible = true;
            EmptyStateMessage = "世代一覧を読み込めません。既存データを確認してください。";
            CanRestore = false;
            CanDelete = false;
            MessageBox.Show("世代一覧の更新に失敗しました。ログを確認してください。", "世代一覧", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    //CSJ,method=UpdateState,状態を更新する
    private void UpdateState()
    {
        var hasItems = Generations.Count > 0;
        IsEmptyStateVisible = !hasItems;
        CanRestore = hasItems && SelectedGeneration is not null;
        CanDelete = hasItems && SelectedGeneration is not null;
        EmptyStateMessage = hasItems
            ? string.Empty
            : "このプロジェクトには保存された世代がありません。";
    }

    //CSJ,method=OnPropertyChanged,プロパティ変更済み時の処理
    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    //CSJ,class=ProjectGenerationListRow,プロジェクト世代一覧行
    public sealed class ProjectGenerationListRow
    {
        //CSJ,property=GenerationId,世代ID
        public string GenerationId { get; set; } = string.Empty;
        //CSJ,property=CreatedAt,作成日時
        public DateTimeOffset CreatedAt { get; set; }
        //CSJ,property=CreatedAtString,作成日時の文字列
        public string CreatedAtString => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
        //CSJ,property=DisplayName,表示名
        public string DisplayName { get; set; } = string.Empty;
        //CSJ,property=Memo,メモ
        public string? Memo { get; set; }
        //CSJ,property=FileSizeText,ファイルサイズテキスト
        public string FileSizeText { get; set; } = string.Empty;
        //CSJ,property=SourceProjectPath,元プロジェクトのパス
        public string SourceProjectPath { get; set; } = string.Empty;
        //CSJ,property=StatusText,状態テキスト
        public string StatusText { get; set; } = string.Empty;

        //CSJ,method=FromRecord,記録から変換する
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
