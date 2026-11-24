using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using YMMProjectManager.Application;
using YMMProjectManager.Application.Thumbnails;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Git;
using YMMProjectManager.Infrastructure.Output;
using YMMProjectManager.Infrastructure.Packaging;
using YMMProjectManager.Infrastructure.Thumbnails;
using YMMProjectManager.Presentation.Commands;
using YMMProjectManager.Presentation.Relink;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;

namespace YMMProjectManager.Presentation.ViewModels;

//CSJ,class=ProjectListViewModel,プロジェクト一覧の表示モデル
public sealed partial class ProjectListViewModel : ViewModelBase, ITimelineToolViewModel
{
    //CSJ,field=logger,ロガー
    private readonly FileLogger logger;
    //CSJ,field=repository,リポジトリ
    private readonly IProjectRepository repository;
    //CSJ,field=seekPreviewThumbnailGenerator,フレーム移動プレビューサムネイル生成器
    private readonly SeekPreviewThumbnailGenerator seekPreviewThumbnailGenerator;
    //CSJ,field=bundleService,一括パッケージサービス
    private readonly YmmpxLibBundleService bundleService;
    //CSJ,field=folders,フォルダー一覧
    private List<ProjectFolder> folders = [];
    //CSJ,field=selectedProject,選択中のプロジェクト
    private ProjectEntry? selectedProject;
    private bool isBusy;
    private bool isInitialized;
    //CSJ,field=projectListRevision,プロジェクト一覧版
    private ProjectListRevision? projectListRevision;
    //CSJ,field=pendingIdentity,保留識別情報
    private PendingIdentity? pendingIdentity;
    //CSJ,field=lastSavedFingerprint,最後保存済みフィンガープリント
    private string? lastSavedFingerprint;
    //CSJ,field=savedBaseline,保存済み基準
    private ProjectStore? savedBaseline;
    private ProjectStore? retainedRecoverySnapshot;
    private ProjectStore? retainedScreenSnapshot;
    private readonly Dictionary<string, (Guid Id, string Hash)> pendingAdditionEvidence = new(StringComparer.OrdinalIgnoreCase);
    private (Guid Id, string Path, ProjectFileIdentity Identity)? pendingExtractedProject;
    private bool isProjectListLossless;
    private bool hasUnsavedProjectList;
    //CSJ,field=bundleStatus,一括パッケージ状態
    private string bundleStatus = string.Empty;
    //CSJ,field=bundleProgress,一括パッケージ進捗
    private double bundleProgress;
    //CSJ,field=gitStatus,Git状態
    private string gitStatus = string.Empty;
    //CSJ,field=gitPath,Gitパス
    private string gitPath = string.Empty;
    //CSJ,field=gitCommitMessage,Gitコミットメッセージ
    private string gitCommitMessage = string.Empty;
    //CSJ,field=gitBranch,Gitブランチ
    private string gitBranch = string.Empty;
    //CSJ,field=gitRemote,Gitリモート
    private string gitRemote = string.Empty;
    //CSJ,field=gitCloneName,Gitクローン名前
    private string gitCloneName = string.Empty;
    //CSJ,field=assetPath,素材のパス
    private string assetPath = string.Empty;
    //CSJ,field=assetStatus,素材状態
    private string assetStatus = string.Empty;
    //CSJ,field=assetUrl,素材URL
    private string assetUrl = string.Empty;
    private IReadOnlyList<AssetMetadata>? inspectedAssets;
    //CSJ,field=toolCancellation,ツールキャンセル
    private CancellationTokenSource? toolCancellation;
    //CSJ,field=activeToolOperation,有効ツール操作
    private string? activeToolOperation;

    //CSJ,property=Projects,プロジェクト一覧
    public ObservableCollection<ProjectEntry> Projects { get; } = [];

    //CSJ,property=IsBusy,処理中か
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    //CSJ,property=SelectedProject,選択中のプロジェクト
    public ProjectEntry? SelectedProject
    {
        get => selectedProject;
        set
        {
            if (SetProperty(ref selectedProject, value))
            {
                GitChanges.Clear();
                GitPath = "";
                GitStatus = string.Empty;
                AssetStatus = string.Empty;
                inspectedAssets = null;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    //CSJ,property=BundleStatus,一括パッケージ状態
    public string BundleStatus
    {
        get => bundleStatus;
        set => SetProperty(ref bundleStatus, value);
    }

    //CSJ,property=BundleProgress,一括パッケージ進捗
    public double BundleProgress
    {
        get => bundleProgress;
        set => SetProperty(ref bundleProgress, value);
    }

    //CSJ,property=HasUnsavedProjectList,プロジェクト一覧に未保存の変更があるか
    public bool HasUnsavedProjectList
    {
        get => hasUnsavedProjectList;
        private set
        {
            if (SetProperty(ref hasUnsavedProjectList, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    //CSJ,property=RetryProjectListSaveCommand,再試行プロジェクト一覧保存コマンド
    public ICommand RetryProjectListSaveCommand { get; }
    //CSJ,property=AssignLegacyProjectIdCommand,旧プロジェクトへのID割り当てコマンド
    public ICommand AssignLegacyProjectIdCommand { get; }
    //CSJ,property=AssociateLegacyMetadataCommand,関連付け旧形式メタデータコマンド
    public ICommand AssociateLegacyMetadataCommand { get; }
    //CSJ,property=RecoverIdentityCommand,復旧識別情報コマンド
    public ICommand RecoverIdentityCommand { get; }
    //CSJ,property=RecoverProjectListCommand,復旧プロジェクト一覧コマンド
    public ICommand RecoverProjectListCommand { get; }
    //CSJ,property=ExportRecoverySnapshotCommand,書き出し復旧スナップショットコマンド
    public ICommand ExportRecoverySnapshotCommand { get; }
    //CSJ,property=ExportRecoveryScreenCommand,復旧画面の書き出しコマンド
    public ICommand ExportRecoveryScreenCommand { get; }
    //CSJ,property=AddCommand,追加コマンド
    public ICommand AddCommand { get; }
    //CSJ,property=RemoveCommand,除去コマンド
    public ICommand RemoveCommand { get; }
    //CSJ,property=OpenCommand,表示コマンド
    public ICommand OpenCommand { get; }
    //CSJ,property=GenerateThumbnailsFastCommand,高速サムネイル生成コマンド
    public ICommand GenerateThumbnailsFastCommand { get; }
    //CSJ,property=RunSeekProbeCommand,実行フレーム移動調査コマンド
    public ICommand RunSeekProbeCommand { get; }
    //CSJ,property=OpenRelinkWindowCommand,表示再リンクウィンドウコマンド
    public ICommand OpenRelinkWindowCommand { get; }
    //CSJ,property=OpenSelectedProjectRelinkCommand,表示選択中のプロジェクト再リンクコマンド
    public ICommand OpenSelectedProjectRelinkCommand { get; }
    //CSJ,property=PackageSelectedProjectCommand,パッケージ選択中のプロジェクトコマンド
    public ICommand PackageSelectedProjectCommand { get; }
    //CSJ,property=PackageOpenedProjectCommand,パッケージ表示済みプロジェクトコマンド
    public ICommand PackageOpenedProjectCommand { get; }
    //CSJ,property=ExtractBundleCommand,抽出一括パッケージコマンド
    public ICommand ExtractBundleCommand { get; }
    //CSJ,property=RefreshGitStatusCommand,更新Git状態コマンド
    public ICommand RefreshGitStatusCommand { get; }
    //CSJ,property=InitializeGitCommand,初期化Git コマンド
    public ICommand InitializeGitCommand { get; }
    //CSJ,property=StageGitPathCommand,パスを指定してGitの変更をステージするコマンド
    public ICommand StageGitPathCommand { get; }
    //CSJ,property=UnstageGitPathCommand,パスを指定してGitのステージを解除するコマンド
    public ICommand UnstageGitPathCommand { get; }
    //CSJ,property=CommitGitCommand,コミットGit コマンド
    public ICommand CommitGitCommand { get; }
    //CSJ,property=CreateGitBranchCommand,作成Gitブランチコマンド
    public ICommand CreateGitBranchCommand { get; }
    //CSJ,property=SwitchGitBranchCommand,Gitブランチ切り替えコマンド
    public ICommand SwitchGitBranchCommand { get; }
    //CSJ,property=FetchGitCommand,フェッチGit コマンド
    public ICommand FetchGitCommand { get; }
    //CSJ,property=PullGitCommand,プルGit コマンド
    public ICommand PullGitCommand { get; }
    //CSJ,property=PushGitCommand,プッシュGit コマンド
    public ICommand PushGitCommand { get; }
    //CSJ,property=GitHistoryCommand,Git履歴コマンド
    public ICommand GitHistoryCommand { get; }
    //CSJ,property=GitOriginCommand,Gitのorigin情報を表示するコマンド
    public ICommand GitOriginCommand { get; }
    //CSJ,property=GitCloneCommand,Gitクローンコマンド
    public ICommand GitCloneCommand { get; }
    //CSJ,property=FindAssetCandidatesCommand,検索素材候補一覧コマンド
    public ICommand FindAssetCandidatesCommand { get; }
    //CSJ,property=InspectAssetCommand,点検素材コマンド
    public ICommand InspectAssetCommand { get; }
    //CSJ,property=ExportAssetMetadataCommand,書き出し素材メタデータコマンド
    public ICommand ExportAssetMetadataCommand { get; }
    //CSJ,property=UpdateAssetMetadataCommand,更新素材メタデータコマンド
    public ICommand UpdateAssetMetadataCommand { get; }
    //CSJ,property=VerifyAssetMetadataCommand,検証素材メタデータコマンド
    public ICommand VerifyAssetMetadataCommand { get; }
    //CSJ,property=CancelToolCommand,キャンセルツールコマンド
    public ICommand CancelToolCommand { get; }
    //CSJ,property=GitStatus,Git状態
    public string GitStatus { get => gitStatus; private set => SetProperty(ref gitStatus, value); }
    //CSJ,property=GitPath,Gitパス
    public string GitPath { get => gitPath; set => SetProperty(ref gitPath, value); }
    //CSJ,property=GitCommitMessage,Gitコミットメッセージ
    public string GitCommitMessage { get => gitCommitMessage; set => SetProperty(ref gitCommitMessage, value); }
    //CSJ,property=GitBranch,Gitブランチ
    public string GitBranch { get => gitBranch; set => SetProperty(ref gitBranch, value); }
    //CSJ,property=GitRemote,Gitリモート
    public string GitRemote { get => gitRemote; set => SetProperty(ref gitRemote, value); }
    //CSJ,property=GitCloneName,Gitクローン名前
    public string GitCloneName { get => gitCloneName; set => SetProperty(ref gitCloneName, value); }
    //CSJ,property=AssetPath,素材のパス
    public string AssetPath { get => assetPath; set { if (SetProperty(ref assetPath, value)) inspectedAssets = null; } }
    //CSJ,property=AssetUrl,素材URL
    public string AssetUrl { get => assetUrl; set { if (SetProperty(ref assetUrl, value)) inspectedAssets = null; } }
    //CSJ,property=AssetStatus,素材状態
    public string AssetStatus { get => assetStatus; private set => SetProperty(ref assetStatus, value); }

    public ProjectListViewModel()
        : this(PluginDiagnostics.Logger, null)
    {
    }

    internal ProjectListViewModel(FileLogger logger, IProjectRepository? repository)
    {
        // この ViewModel には、安定しているサムネイル経路だけを持たせる。
        this.logger = logger;
        this.repository = repository ?? new JsonProjectRepository(logger);
        //CSJ,variable=currentPreviewCaptureService,現在のプレビューキャプチャサービス
        var currentPreviewCaptureService = new CurrentPreviewCaptureService(logger);
        seekPreviewThumbnailGenerator = new SeekPreviewThumbnailGenerator(logger, currentPreviewCaptureService);
        bundleService = new YmmpxLibBundleService(logger);

        RetryProjectListSaveCommand = new AsyncRelayCommand(RetryProjectListSaveAsync, () => isInitialized && !IsBusy && HasUnsavedProjectList && retainedRecoverySnapshot is null);
        AssignLegacyProjectIdCommand = new AsyncRelayCommand(AssignLegacyProjectIdAsync, () => isInitialized && !IsBusy && !HasUnsavedProjectList && SelectedProject is { ProjectId: null });
        AssociateLegacyMetadataCommand = new AsyncRelayCommand(ChooseLegacyMetadataAsync, () => isInitialized && !IsBusy && !HasUnsavedProjectList && SelectedProject is { ProjectId: null });
        RecoverIdentityCommand = new AsyncRelayCommand(RecoverIdentityAsync, () => isInitialized && !IsBusy && HasUnsavedProjectList && pendingIdentity is not null);
        RecoverProjectListCommand = new AsyncRelayCommand(RecoverProjectListAsync, () => isInitialized && !IsBusy && HasUnsavedProjectList && savedBaseline is not null);
        ExportRecoverySnapshotCommand = new AsyncRelayCommand(ExportRecoverySnapshotAsync, () => !IsBusy && retainedRecoverySnapshot is not null);
        ExportRecoveryScreenCommand = new AsyncRelayCommand(ExportRecoveryScreenAsync, () => !IsBusy && retainedScreenSnapshot is not null);
        AddCommand = new AsyncRelayCommand(() => AddProjectsAsync(), () => isInitialized && !IsBusy && retainedRecoverySnapshot is null);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, () => !IsBusy && retainedRecoverySnapshot is null && SelectedProject is not null);
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => !IsBusy && retainedRecoverySnapshot is null && SelectedProject is not null);
        GenerateThumbnailsFastCommand = new AsyncRelayCommand(GenerateThumbnailsFastAsync, () => !IsBusy && SelectedProject is not null);
        RunSeekProbeCommand = new AsyncRelayCommand(RunSeekProbeAsync, () => !IsBusy);
        OpenRelinkWindowCommand = new AsyncRelayCommand(OpenOpenedProjectRelinkWindowAsync, () => !IsBusy && TimelineContextService.Timeline is not null);
        OpenSelectedProjectRelinkCommand = new AsyncRelayCommand(OpenSelectedProjectRelinkAsync, () => !IsBusy && SelectedProject is not null);
        PackageSelectedProjectCommand = new AsyncRelayCommand(PackageSelectedProjectAsync, () => !IsBusy && SelectedProject is not null);
        PackageOpenedProjectCommand = new AsyncRelayCommand(PackageOpenedProjectAsync, () => !IsBusy && TimelineContextService.Timeline is not null);
        // The external extractor's destination and cleanup guarantees are not yet verified.
        ExtractBundleCommand = new AsyncRelayCommand(ExtractBundleAsync, () => !IsBusy);
        ExportLegacyGenerationCommand = new AsyncRelayCommand(ExportLegacyGenerationAsync, () => !IsBusy);
        CollectAssetToPoolCommand = new AsyncRelayCommand(CollectAssetToPoolAsync, () => !IsBusy && SelectedProject is not null);
        InspectGitHubAccessCommand = new AsyncRelayCommand(InspectGitHubAccessAsync, () => !IsBusy && SelectedProject is not null);
        GitHubBrowserLoginCommand = new AsyncRelayCommand(() => RunGitHubBrowserLoginAsync(reauthenticate: false), () => !IsBusy);
        GitHubBrowserReloginCommand = new AsyncRelayCommand(() => RunGitHubBrowserLoginAsync(reauthenticate: true), () => !IsBusy);
        CancelGitHubBrowserLoginCommand = new RelayCommand(() =>
        {
            if (activeToolOperation == GitHubBrowserLoginOperation)
                toolCancellation?.Cancel();
        },
            () => activeToolOperation == GitHubBrowserLoginOperation && toolCancellation is { IsCancellationRequested: false });
        RefreshGitStatusCommand = new AsyncRelayCommand(() => ShowGitStatusAsync(false), () => !IsBusy && SelectedProject is not null);
        InitializeGitCommand = new AsyncRelayCommand(() => ShowGitStatusAsync(true), () => !IsBusy && SelectedProject is not null);
        StageGitPathCommand = new AsyncRelayCommand(() => ApplyWorkingTreeActionAsync(WorkingTreeAction.Stage), () => !IsBusy && SelectedProject is not null);
        UnstageGitPathCommand = new AsyncRelayCommand(() => ApplyWorkingTreeActionAsync(WorkingTreeAction.Unstage), () => !IsBusy && SelectedProject is not null);
        CommitGitCommand = new AsyncRelayCommand(() => ApplyWorkingTreeActionAsync(WorkingTreeAction.Commit), () => !IsBusy && SelectedProject is not null);
        CreateGitBranchCommand = new AsyncRelayCommand(() => ApplyWorkingTreeActionAsync(WorkingTreeAction.CreateBranch), () => !IsBusy && SelectedProject is not null);
        SwitchGitBranchCommand = new AsyncRelayCommand(() => ApplyWorkingTreeActionAsync(WorkingTreeAction.SwitchBranch), () => !IsBusy && SelectedProject is not null);
        FetchGitCommand = new AsyncRelayCommand(() => RunTrustedRemoteAsync(GitRemoteOperation.Fetch), () => !IsBusy && SelectedProject is not null);
        PullGitCommand = new AsyncRelayCommand(RunConfirmedGitPullAsync, () => !IsBusy && SelectedProject is not null);
        InspectGitPullCommand = new AsyncRelayCommand(InspectGitPullAsync, () => !IsBusy && SelectedProject is not null);
        PushGitCommand = new AsyncRelayCommand(() => RunTrustedRemoteAsync(GitRemoteOperation.Push), () => !IsBusy && SelectedProject is not null);
        GitHistoryCommand = new AsyncRelayCommand(() => ShowGitDetailsAsync(false), () => !IsBusy && SelectedProject is not null);
        GitOriginCommand = new AsyncRelayCommand(() => ShowGitDetailsAsync(true), () => !IsBusy && SelectedProject is not null);
        GitCloneCommand = new AsyncRelayCommand(CloneGitAsync, () => !IsBusy);
        FindAssetCandidatesCommand = new AsyncRelayCommand(FindAssetCandidatesAsync, () => !IsBusy && SelectedProject is not null);
        InspectAssetCommand = new AsyncRelayCommand(InspectAssetAsync, () => !IsBusy && SelectedProject is not null);
        InspectExternalAssetCommand = new AsyncRelayCommand(InspectExternalAssetAsync, () => !IsBusy && SelectedProject is not null);
        VerifyStoredExternalAssetsCommand = new AsyncRelayCommand(VerifyStoredExternalAssetsAsync,
            () => !IsBusy && SelectedProject?.MetadataAssociation is not null);
        ExportAssetMetadataCommand = new AsyncRelayCommand(ExportAssetMetadataAsync, () => !IsBusy && SelectedProject is not null);
        UpdateAssetMetadataCommand = new AsyncRelayCommand(() => SaveAssetMetadataAsync(true), () => !IsBusy && SelectedProject is not null);
        VerifyAssetMetadataCommand = new AsyncRelayCommand(VerifyAssetMetadataAsync, () => !IsBusy && SelectedProject is not null);
        SelectMetadataSeriesCommand = new AsyncRelayCommand(SelectMetadataSeriesAsync, () => !IsBusy && !HasUnsavedProjectList && SelectedProject?.ProjectId is not null);
        ImportMetadataSeriesCommand = new AsyncRelayCommand(ImportMetadataSeriesAsync, () => isInitialized && !IsBusy && !HasUnsavedProjectList && SelectedProject?.ProjectId is not null);
        InspectMetadataSeriesCommand = new AsyncRelayCommand(InspectMetadataSeriesAsync, () => !IsBusy && SelectedProject?.MetadataAssociation is not null);
        RebindMetadataDirectoryCommand = new AsyncRelayCommand(RebindMetadataDirectoryAsync,
            () => !IsBusy && !HasUnsavedProjectList && SelectedProject?.MetadataAssociation is not null);
        ReassociateProjectCommand = new AsyncRelayCommand(ReassociateProjectAsync, () => !IsBusy && !HasUnsavedProjectList && SelectedProject?.ProjectId is not null);
        VerifyProjectIdentityCommand = new AsyncRelayCommand(VerifyProjectIdentityAsync, () => !IsBusy && !HasUnsavedProjectList && SelectedProject?.ProjectId is not null);
        RecoverProjectListPublicationCommand = new AsyncRelayCommand(RecoverProjectListPublicationAsync,
            () => !IsBusy && repository is JsonProjectRepository);
        ReviewDraftCommand = new AsyncRelayCommand(ReviewDraftAsync, () => isInitialized && !IsBusy && !HasUnsavedProjectList && repository is JsonProjectRepository);
        ExportOriginalProjectListCommand = new AsyncRelayCommand(ExportOriginalProjectListAsync,
            () => !IsBusy && repository is JsonProjectRepository && (!isInitialized || !isProjectListLossless));
        RepairProjectListIdentitiesCommand = new AsyncRelayCommand(RepairProjectListIdentitiesAsync,
            () => !IsBusy && !HasUnsavedProjectList && repository is JsonProjectRepository);
        CancelToolCommand = new RelayCommand(() => toolCancellation?.Cancel(), () => toolCancellation is not null && !toolCancellation.IsCancellationRequested);
    }

    //CSJ,method=ExecuteToolAsync,ツールを実行する（非同期）
    private async Task ExecuteToolAsync(string name, Func<CancellationToken, Task> action)
    {
        await ExecuteWithBusyAsync(name, async () =>
        {
            //CSJ,variable=cancellation,キャンセル
            using var cancellation = new CancellationTokenSource();
            toolCancellation = cancellation;
            activeToolOperation = name;
            CommandManager.InvalidateRequerySuggested();
            try { await action(cancellation.Token); }
            catch (OperationCanceledException ex)
            {
                //CSJ,variable=recovery,復旧
                var recovery = ex.Data["CloneRecoveryPath"] as string;
                //CSJ,variable=message,メッセージ
                var message = "中止しました。ディスクやリモートの状態を確認してください。";
                if (recovery is not null) message += " 残存 clone: " + recovery;
                if (ex.Data["YmmpxRetainedStagingPath"] is string staging) message += $" 展開一時フォルダー: {staging}";
                if (name == InspectListPublicationOperation)
                    ProjectListPublicationStatus = "中止しました。保存記録を確認するには、もう一度実行してください。";
                else if (name == PullWorkflowOperation)
                    GitStatus = "Pull を中止しました。ブランチ、index、作業ファイルの更新は開始していません。";
                else if (name == GitHubAccessOperation)
                    GitStatus = "GitHubへのアクセス確認を中止しました。";
                else if (name == GitHubBrowserLoginOperation)
                    GitStatus = "GitHubブラウザ認証を中止しました。";
                else if (name.StartsWith("Asset", StringComparison.Ordinal)) AssetStatus = message;
                else if (name.StartsWith("Bundle", StringComparison.Ordinal)) BundleStatus = message;
                else GitStatus = message;
                throw;
            }
            finally
            {
                toolCancellation = null;
                activeToolOperation = null;
                CommandManager.InvalidateRequerySuggested();
            }
        });
    }

    //CSJ,method=SetTimelineToolInfo,タイムラインツール情報を設定する
    public void SetTimelineToolInfo(TimelineToolInfo info)
    {
        TimelineContextService.Info = info;
        //CSJ,variable=timeline,タイムライン
        var timeline = info.Timeline;
        logger.Info($"SetTimelineToolInfo called. timeline={(timeline is null ? "null" : "available")}, currentFrame={timeline?.CurrentFrame}, length={timeline?.Length}");
        CommandManager.InvalidateRequerySuggested();
    }

    //CSJ,method=InitializeAsync,初期化する（非同期）
    public async Task InitializeAsync()
    {
        if (isInitialized)
        {
            return;
        }

        await ExecuteWithBusyAsync("Load", async () =>
        {
            logger.Info("Load start.");
            //CSJ,variable=store,保管先
            ProjectStore store;
            try { store = await repository.LoadAsync().ConfigureAwait(true); }
            catch (ProjectListBusyException ex)
            {
                logger.Error(ex, "Project list initialization was blocked by another operation.");
                ProjectListPublicationStatus = "別の処理が一覧を使用中です。その処理が終わったら「中断された一覧保存を確認」から読み込みを再試行してください。";
                return;
            }
            catch (InvalidDataException ex) when (repository is JsonProjectRepository json && json.HasInterruptedPublication)
            {
                logger.Error(ex, "Project list initialization requires publication recovery.");
                ProjectListPublicationStatus = "中断された一覧保存があります。「中断された一覧保存を確認」を使ってください。";
                return;
            }
            projectListRevision = store.Revision;
            isProjectListLossless = store.IsRecoverySafe;

            Projects.Clear();
            foreach (var item in store.Projects)
            {
                Projects.Add(item);
            }

            folders = store.Folders.ToList();
            lastSavedFingerprint = CurrentStoreFingerprint();
            savedBaseline = ProjectListMerge.Snapshot(store);

            isInitialized = true;
            CommandManager.InvalidateRequerySuggested();
            logger.Info($"Load end. count={Projects.Count}");
            PrepareThumbnailMetadata(Projects.ToList());
        }).ConfigureAwait(true);
    }

    //CSJ,method=AddProjectsAsync,プロジェクト一覧を追加する（非同期）
    public async Task AddProjectsAsync(IEnumerable<string>? explicitPaths = null)
    {
        await ExecuteWithBusyAsync("Add batch", async () =>
        {
            if (!isInitialized) throw new InvalidOperationException("プロジェクト一覧が読み込まれていません。");
            //CSJ,variable=paths,パス一覧
            var paths = explicitPaths?.ToArray();
            if (paths is null)
            {
                //CSJ,variable=dialog,ダイアログ
                var dialog = new OpenFileDialog
                {
                    Filter = "YMM Project (*.ymmp)|*.ymmp",
                    CheckFileExists = true,
                    Multiselect = true,
                    Title = "Select .ymmp projects",
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                paths = dialog.FileNames;
            }

            //CSJ,variable=existing,既存
            var existing = new HashSet<string>(Projects.Select(x => x.FullPath), StringComparer.OrdinalIgnoreCase);
            var addedEntries = new List<ProjectEntry>();
            //CSJ,variable=rawPath,生データパス
            foreach (var rawPath in paths)
            {
                if (!rawPath.EndsWith(".ymmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                //CSJ,variable=fullPath,完全パス
                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(rawPath);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, $"Add batch skip. invalid path: {rawPath}");
                    continue;
                }

                if (!File.Exists(fullPath))
                {
                    logger.Info($"Add batch skip. file not found: {fullPath}");
                    continue;
                }

                if (!existing.Add(fullPath))
                {
                    continue;
                }

                var entry = new ProjectEntry
                {
                    FullPath = fullPath,
                    ProjectId = Guid.NewGuid(),
                    DisplayName = Path.GetFileNameWithoutExtension(fullPath),
                };
                try
                {
                    //CSJ,variable=evidence,証跡
                    var evidence = await ProjectFileEvidence.ReadAsync(fullPath);
                    if (Projects.Any(other => other.FileIdentity is { } recorded && recorded.IsSameFile(evidence)))
                    {
                        AssetStatus = $"同じファイルが別のパスから登録されています。追加を中止しました。\n{fullPath}";
                        continue;
                    }
                    entry.FileIdentity = evidence;
                    pendingAdditionEvidence[fullPath] = (entry.ProjectId!.Value, evidence.Sha256);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    logger.Info("Project recovery evidence unavailable; addition can be saved normally but not automatically recovered.");
                }
                if (Projects.Any(item => string.Equals(item.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))) continue;
                entry.LinkedYmmpFiles.Add(new LinkedYmmpFile { FilePath = fullPath, DisplayName = entry.DisplayName, Role = YmmpRole.Main, Exists = true, LastCheckedAt = DateTimeOffset.Now });
                Projects.Add(entry);
                SelectedProject = entry;
                addedEntries.Add(entry);
            }

            if (addedEntries.Count > 0)
            {
                await SaveAsync().ConfigureAwait(true);
                PrepareThumbnailMetadata(addedEntries);
            }

            logger.Info($"Add batch end. added={addedEntries.Count}, total={Projects.Count}");
        }).ConfigureAwait(true);
    }

    //CSJ,method=RemoveAsync,除去する（非同期）
    private async Task RemoveAsync()
    {
        if (SelectedProject is null)
        {
            return;
        }

        await ExecuteWithBusyAsync("Remove", async () =>
        {
            var target = SelectedProject;
            if (target is null)
            {
                return;
            }

            Projects.Remove(target);
            SelectedProject = null;
            await SaveAsync().ConfigureAwait(true);
            logger.Info($"Remove end. removed={target.FullPath}");
        }).ConfigureAwait(true);
    }

    //CSJ,method=OpenAsync,開く（非同期）
    private async Task OpenAsync()
    {
        if (SelectedProject is null)
        {
            return;
        }

        await ExecuteWithBusyAsync("Open", async () =>
        {
            //CSJ,variable=project,プロジェクト
            var project = SelectedProject;
            if (project is null)
            {
                return;
            }

            if (!TryGetOpenableProjectPath(project.FullPath, out var pathToOpen, out var reason))
            {
                logger.Info($"Open skipped. path={project.FullPath}, reason={reason}");
                MessageBox.Show(reason, "YMM Project Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = pathToOpen,
                UseShellExecute = true,
            });

            project.LastAccess = DateTimeOffset.Now;
            await SaveAsync().ConfigureAwait(true);
            logger.Info($"Open end. path={pathToOpen}");
        }).ConfigureAwait(true);
    }

    //CSJ,method=RetryProjectListSaveAsync,プロジェクト一覧の保存を再試行する（非同期）
    public async Task RetryProjectListSaveAsync()
    {
        if (!HasUnsavedProjectList) return;
        await ExecuteWithBusyAsync("Retry project list save", SaveAsync).ConfigureAwait(true);
    }

    private async Task AssignLegacyProjectIdAsync()
    {
        //CSJ,variable=project,プロジェクト
        var project = SelectedProject;
        if (project is null || project.ProjectId is not null || HasUnsavedProjectList) return;
        await ExecuteWithBusyAsync("Assign project identity", async () =>
        {
            if (!isProjectListLossless) { AssetStatus = "一覧に安全に保存できない項目があります。ID の設定を中止しました。"; return; }
            if (!File.Exists(project.FullPath)) { AssetStatus = "プロジェクトが見つかりません。移動先の関連付けは未対応です。"; return; }
            //CSJ,variable=projectPath,プロジェクトのパス
            var projectPath = project.FullPath;
            var expected = await ProjectFileEvidence.ReadAsync(projectPath);
            if (MessageBox.Show($"次の一覧項目に新しい ID を設定しますか？\n{project.FullPath}\n既存 metadata は関連付けられません。", "プロジェクト ID", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            //CSJ,variable=file,ファイル
            await using var file = ProjectFileEvidence.OpenRead(projectPath);
            var confirmed = await ProjectFileEvidence.ReadAsync(file);
            if (!ReferenceEquals(project, SelectedProject) || project.ProjectId is not null || project.FullPath != projectPath || confirmed != expected) return;
            SetPendingIdentity(project, Guid.NewGuid(), confirmed);
            await SaveAsync().ConfigureAwait(true);
            AssetStatus = HasUnsavedProjectList ? "ID を保存しましたが、保存中の一覧変更が未保存です。" : "プロジェクト ID を保存しました。既存 metadata の関連付けは別途必要です。";
        }).ConfigureAwait(true);
    }

    private async Task ChooseLegacyMetadataAsync()
    {
        //CSJ,variable=project,プロジェクト
        var project = SelectedProject;
        if (project is null || project.ProjectId is not null || HasUnsavedProjectList) return;
        if (!isProjectListLossless) { AssetStatus = "一覧に安全に保存できない項目があります。関連付けを中止しました。"; return; }
        //CSJ,variable=dialog,ダイアログ
        var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json", Title = "関連付ける旧素材メタデータを選択", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        await ExecuteWithBusyAsync("Associate legacy metadata", async () =>
        {
            //CSJ,variable=projectPath,プロジェクトのパス
            var projectPath = project.FullPath;
            //CSJ,variable=projectEvidence,プロジェクト証跡
            var projectEvidence = await ProjectFileEvidence.ReadAsync(projectPath);
            var before = await ReadMetadataEvidenceAsync(dialog.FileName).ConfigureAwait(true);
            //CSJ,variable=manifest,目録
            var manifest = AssetManifestStore.Parse(before);
            //CSJ,variable=afterLoad,変更後読み込み
            var afterLoad = await ReadMetadataEvidenceAsync(dialog.FileName).ConfigureAwait(true);
            if (!before.SequenceEqual(afterLoad)) throw new InvalidDataException("metadata が変更されました。再選択してください。");
            if (!ReferenceEquals(project, SelectedProject) || project.ProjectId is not null || HasUnsavedProjectList || project.FullPath != projectPath) return;
            //CSJ,variable=confirm,確認
            var confirm = MessageBox.Show($"一覧項目: {projectPath}\n選択 metadata: {dialog.FileName}\nmetadata の project 名: {manifest.ProjectFileName}\nproject ID: {manifest.ProjectId}\nrevision ID: {manifest.RevisionId}\n\nmetadata の場所は保存しません。今後もファイルを選択する必要があります。名前や内容だけでは同一性を確認できません。同じ論理プロジェクトとして関連付けますか？", "旧素材メタデータ", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;
            //CSJ,variable=afterConfirm,変更後確認
            var afterConfirm = await ReadMetadataEvidenceAsync(dialog.FileName).ConfigureAwait(true);
            if (!before.SequenceEqual(afterConfirm) || project.FullPath != projectPath) throw new InvalidDataException("metadata または project が変更されました。再確認してください。");
            await AssociateLegacyMetadataAsync(project, manifest, projectEvidence).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    //CSJ,method=ReadMetadataEvidenceAsync,メタデータ証跡を読み取る（非同期）
    private static async Task<byte[]> ReadMetadataEvidenceAsync(string path)
    {
        //CSJ,variable=stream,ストリーム
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("素材 metadata が大きすぎます。");
        //CSJ,variable=bytes,バイト列
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes).ConfigureAwait(false);
        return bytes;
    }

    //CSJ,method=AssociateLegacyMetadataAsync,関連付け旧形式メタデータ（非同期）
    internal async Task AssociateLegacyMetadataAsync(ProjectEntry project, AssetManifest manifest, ProjectFileIdentity? expectedProject = null)
    {
        if (!isInitialized || !ReferenceEquals(project, SelectedProject) || project.ProjectId is not null || HasUnsavedProjectList)
            throw new InvalidOperationException("一覧の状態が変わりました。再確認してください。");
        if (!isProjectListLossless) throw new InvalidDataException("一覧に安全に保存できない項目があります。関連付けを中止しました。");
        if (manifest.ProjectId == Guid.Empty || !File.Exists(project.FullPath))
            throw new InvalidDataException("プロジェクトまたは metadata ID が不正です。");
        if (Projects.Any(item => !ReferenceEquals(item, project) && item.ProjectId == manifest.ProjectId))
            throw new InvalidDataException("この metadata の ID は別の一覧項目に使用されています。");
        //CSJ,variable=projectPath,プロジェクトのパス
        var projectPath = project.FullPath;
        //CSJ,variable=file,ファイル
        await using var file = ProjectFileEvidence.OpenRead(projectPath);
        //CSJ,variable=projectEvidence,プロジェクト証跡
        var projectEvidence = await ProjectFileEvidence.ReadAsync(file);
        if (expectedProject is not null && projectEvidence != expectedProject) throw new InvalidDataException("project が変更されました。再確認してください。");
        if (!ReferenceEquals(project, SelectedProject) || project.FullPath != projectPath || project.ProjectId is not null || HasUnsavedProjectList)
            throw new InvalidDataException("project または一覧が変更されました。再確認してください。");
        SetPendingIdentity(project, manifest.ProjectId, projectEvidence);
        await SaveAsync().ConfigureAwait(true);
        AssetStatus = HasUnsavedProjectList ? "ID を保存しましたが、保存中の一覧変更が未保存です。" : "既存の project ID を一覧に関連付けました。metadata と project ファイルは変更していません。";
    }

    //CSJ,record=PendingIdentity,保留識別情報
    private sealed record PendingIdentity(string Path, Guid Id, string Fingerprint, ProjectFileIdentity Evidence);

    //CSJ,method=CurrentStoreFingerprint,現在の保管先フィンガープリント
    private string CurrentStoreFingerprint() => ProjectListRecovery.Fingerprint(new ProjectStore { Projects = Projects.ToList(), Folders = folders.ToList() });

    //CSJ,method=SetPendingIdentity,保留識別情報を設定する
    private void SetPendingIdentity(ProjectEntry project, Guid identity, ProjectFileIdentity evidence)
    {
        if (Projects.Any(item => !ReferenceEquals(item, project) && item.FileIdentity?.IsSameFile(evidence) == true))
            throw new InvalidDataException("同じファイルが別のパスから登録されています。");
        var before = CurrentStoreFingerprint();
        //CSJ,variable=canRecover,復旧の可否
        var canRecover = !HasUnsavedProjectList && lastSavedFingerprint is not null && before == lastSavedFingerprint;
        project.ProjectId = identity;
        project.FileIdentity = evidence;
        var current = new ProjectStore { Projects = Projects.ToList(), Folders = folders.ToList() };
        pendingIdentity = canRecover && ProjectListRecovery.Fingerprint(current, project.FullPath) == before
            ? new PendingIdentity(project.FullPath, identity, ProjectListRecovery.Fingerprint(current), evidence)
            : null;
    }

    //CSJ,method=RecoverIdentityAsync,識別情報を復旧する（非同期）
    private async Task RecoverIdentityAsync()
    {
        //CSJ,variable=pending,保留
        var pending = pendingIdentity;
        if (pending is null || !HasUnsavedProjectList) return;
        await ExecuteWithBusyAsync("Recover project identity", async () =>
        {
            if (CurrentStoreFingerprint() != pending.Fingerprint) throw new InvalidDataException("未保存の一覧に他の変更があるため自動再適用できません。");
            //CSJ,variable=latest,最新
            var latest = await repository.LoadAsync().ConfigureAwait(true);
            //CSJ,variable=file,ファイル
            await using var file = ProjectFileEvidence.OpenRead(pending.Path);
            if (CurrentStoreFingerprint() != pending.Fingerprint || await ProjectFileEvidence.ReadAsync(file) != pending.Evidence)
                throw new InvalidDataException("未保存の一覧または project が変更されました。");
            ProjectListRecovery.ApplyIdentity(latest, pending.Path, pending.Id, pending.Evidence);
            //CSJ,variable=previousPaths,直前のパス一覧
            var previousPaths = Projects.Select(x => x.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            //CSJ,variable=latestPaths,最新パス一覧
            var latestPaths = latest.Projects.Select(x => x.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = latestPaths.Except(previousPaths, StringComparer.OrdinalIgnoreCase).Count();
            var removed = previousPaths.Except(latestPaths, StringComparer.OrdinalIgnoreCase).Count();
            //CSJ,variable=confirm,確認
            var confirm = MessageBox.Show($"最新一覧では追加 {added} 件、削除 {removed} 件です。その他の項目や属性の変更内容は表示しません。\n対象: {pending.Path}\n保留中の ID: {pending.Id}\n最新一覧の内容を維持して、この ID だけ再適用しますか？", "一覧の競合回復", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;
            if (CurrentStoreFingerprint() != pending.Fingerprint) throw new InvalidDataException("未保存の一覧が変更されました。");
            var retained = ProjectListMerge.Snapshot(new ProjectStore { Projects = Projects.ToList(), Folders = folders.ToList(), Revision = projectListRevision, IsRecoverySafe = isProjectListLossless });
            await SaveRepositoryWithPendingDraftAsync(latest);
            projectListRevision = latest.Revision;
            isProjectListLossless = latest.IsRecoverySafe;
            if (CurrentStoreFingerprint() != pending.Fingerprint)
            {
                FreezePartialRecovery(retained, "保存中に未保存の一覧が変わりました。");
                return;
            }
            if (!AdoptRecoveredStore(latest, retained, pending.Path)) return;
            pendingIdentity = null;
            lastSavedFingerprint = CurrentStoreFingerprint();
            savedBaseline = ProjectListMerge.Snapshot(latest);
            HasUnsavedProjectList = false;
            PrepareThumbnailMetadata(Projects.ToList());
            AssetStatus = "最新一覧へ ID を再適用しました。";
        }).ConfigureAwait(true);
    }

    //CSJ,method=RecoverProjectListAsync,プロジェクト一覧を復旧する（非同期）
    private async Task RecoverProjectListAsync()
    {
        //CSJ,variable=baseline,基準
        var baseline = savedBaseline;
        if (baseline is null || !HasUnsavedProjectList) return;
        await ExecuteWithBusyAsync("Recover project list", async () =>
        {
            //CSJ,variable=local,ローカル
            var local = ProjectListMerge.Snapshot(new ProjectStore { Projects = Projects.ToList(), Folders = folders.ToList(), Revision = projectListRevision, IsRecoverySafe = isProjectListLossless });
            //CSJ,variable=fingerprint,フィンガープリント
            var fingerprint = ProjectListRecovery.Fingerprint(local);
            //CSJ,variable=latest,最新
            var latest = await repository.LoadAsync().ConfigureAwait(true);
            if (CurrentStoreFingerprint() != fingerprint) throw new InvalidDataException("未保存の一覧が変更されました。再確認してください。");
            //CSJ,variable=plan,計画
            var plan = MergeWithReview(baseline, local, latest);
            if (plan is null) return;
            if (plan.Conflicts.Count != 0) throw new InvalidDataException(string.Join("\n", plan.Conflicts.Take(20)) + (plan.Conflicts.Count > 20 ? "\n他にも競合があります。" : ""));
            if (plan.Changes.Count == 0) throw new InvalidDataException("再適用する変更がありません。ID 設定は専用の回復操作を使用してください。");
            if (plan.Changes.Count > 20) throw new InvalidDataException("変更が20件を超えています。すべてを表示できないため再適用を中止しました。");
            var merged = plan.Store!;
            //CSJ,variable=verifiedFiles,検証済みファイル一覧
            using var verifiedFiles = await ProjectListRecoveryFiles.OpenAsync(merged, latest,
                //CSJ,parameter=path,パス
                path => pendingAdditionEvidence.TryGetValue(path, out var evidence) ? evidence : null);
            //CSJ,variable=summary,概要
            var summary = string.Join("\n", plan.Changes);
            if (summary.Length > 12000) throw new InvalidDataException("変更内容が大きすぎて確認できないため再適用を中止しました。");
            //CSJ,variable=confirm,確認
            var confirm = MessageBox.Show($"最新一覧を保持し、次の未保存変更を再適用します。最新側の属性変更は表示されません。\n{summary}\n続行しますか？", "一覧の競合回復", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;
            if (CurrentStoreFingerprint() != fingerprint) throw new InvalidDataException("未保存の一覧が変更されました。");
            await SaveRepositoryWithPendingDraftAsync(merged);
            if (CurrentStoreFingerprint() != fingerprint)
            {
                FreezePartialRecovery(local, "保存中に未保存の一覧が変わりました。");
                return;
            }
            projectListRevision = merged.Revision; isProjectListLossless = merged.IsRecoverySafe;
            if (!AdoptRecoveredStore(merged, local, null)) return;
            savedBaseline = ProjectListMerge.Snapshot(merged); pendingIdentity = null;
            pendingAdditionEvidence.Clear();
            pendingExtractedProject = null;
            lastSavedFingerprint = CurrentStoreFingerprint(); HasUnsavedProjectList = false;
            PrepareThumbnailMetadata(Projects.ToList());
            AssetStatus = "最新一覧へ未保存変更を再適用しました。";
        }).ConfigureAwait(true);
    }

    private bool AdoptRecoveredStore(ProjectStore saved, ProjectStore originalLocal, string? selectionPath)
    {
        try
        {
            var expected = ProjectListRecovery.Fingerprint(saved);
            Projects.Clear(); foreach (var item in saved.Projects) Projects.Add(item);
            folders = saved.Folders.ToList();
            SelectedProject = selectionPath is null ? null : Projects.Single(p => string.Equals(p.FullPath, selectionPath, StringComparison.OrdinalIgnoreCase));
            if (CurrentStoreFingerprint() != expected) throw new InvalidOperationException("画面更新中に一覧が変更されました。");
            return true;
        }
        catch (Exception ex)
        {
            FreezePartialRecovery(originalLocal, "画面更新中に一覧が変更されたか、更新に失敗しました。");
            logger.Error(ex, "Project list saved but UI recovery did not complete.");
            return false;
        }
    }

    private void FreezePartialRecovery(ProjectStore originalLocal, string reason)
    {
        retainedRecoverySnapshot = ProjectListMerge.Snapshot(originalLocal);
        projectListRevision = null; savedBaseline = null; pendingIdentity = null; lastSavedFingerprint = null;
        HasUnsavedProjectList = true;
        try { retainedScreenSnapshot = ProjectListMerge.Snapshot(new ProjectStore { Projects = Projects.ToList(), Folders = folders.ToList(), IsRecoverySafe = isProjectListLossless }); }
        catch (Exception ex) { retainedScreenSnapshot = null; logger.Error(ex, "Could not snapshot the changed UI state."); }
        AssetStatus = reason + (retainedScreenSnapshot is null
            ? "再適用は保存されています。回復前の一覧を保持しましたが、現在の画面状態は退避できません。画面内容を確認してください。"
            : "再適用は保存されています。回復前の未保存一覧と現在の画面状態をメモリに保持しています。各退避ボタンで別名保存し、内容を確認してください。");
        CommandManager.InvalidateRequerySuggested();
    }

    //CSJ,method=ExportRecoverySnapshotAsync,復旧スナップショットを書き出す（非同期）
    private async Task ExportRecoverySnapshotAsync()
    {
        //CSJ,variable=snapshot,スナップショット
        var snapshot = retainedRecoverySnapshot;
        if (snapshot is null) return;
        await ExportSnapshotAsync(snapshot).ConfigureAwait(true);
    }

    private async Task ExportRecoveryScreenAsync()
    {
        //CSJ,variable=snapshot,スナップショット
        var snapshot = retainedScreenSnapshot;
        if (snapshot is null) return;
        await ExportSnapshotAsync(snapshot).ConfigureAwait(true);
    }

    //CSJ,method=ExportSnapshotAsync,スナップショットを書き出す（非同期）
    private async Task ExportSnapshotAsync(ProjectStore snapshot)
    {
        //CSJ,variable=dialog,ダイアログ
        var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", Title = "未保存一覧を新しいファイルに退避", OverwritePrompt = false };
        if (dialog.ShowDialog() != true) return;
        await ExecuteWithBusyAsync("Export recovery snapshot", async () =>
        {
            var exporter = new JsonProjectRepository(logger, dialog.FileName);
            //CSJ,variable=copy,コピー
            var copy = ProjectListMerge.Snapshot(snapshot);
            await exporter.SaveNewCopyAsync(copy).ConfigureAwait(true);
            AssetStatus = "未保存一覧を新しいファイルへ退避しました。元の画面と一覧は変更していません。";
        }).ConfigureAwait(true);
    }

    //CSJ,method=SaveAsync,保存する（非同期）
    private async Task SaveAsync()
    {
        if (!isInitialized)
        {
            throw new InvalidOperationException("プロジェクト一覧を読み込めなかったため保存できません。");
        }

        try
        {
            if (!isProjectListLossless) throw new InvalidDataException("一覧に安全に保存できない項目があります。元ファイルを保護するため保存を中止しました。");
            //CSJ,variable=store,保管先
            var store = ProjectListMerge.Snapshot(new ProjectStore
            {
                Projects = Projects.ToList(),
                Folders = folders.ToList(),
                Revision = projectListRevision,
                IsRecoverySafe = isProjectListLossless,
            });
            //CSJ,variable=expectedFingerprint,期待値フィンガープリント
            var expectedFingerprint = ProjectListRecovery.Fingerprint(store);
            var extracted = pendingExtractedProject;
            if (extracted is { } pending)
            {
                var registration = store.Projects.SingleOrDefault(project => project.ProjectId == pending.Id);
                if (registration is null)
                    extracted = null; // The user removed the pending registration.
                else if (registration.FullPath != pending.Path || registration.FileIdentity != pending.Identity)
                    throw new IOException("展開したPFの一覧項目が変更されたため、登録できません。");
            }
            //CSJ,variable=directory,ディレクトリ
            using var directory = extracted is { } destination
                ? OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(destination.Path)!, enforceShare: true) : null;
            //CSJ,variable=file,ファイル
            await using var file = extracted is { } source ? YmmpxTransfer.OpenOrdinaryRead(source.Path) : null;
            if (extracted is { } original && await ProjectFileEvidence.ReadAsync(file!) != original.Identity)
                throw new IOException("展開したPFが変更されたため、一覧に登録できません。");
            await SavePendingDraftAsync(store);
            await SaveRepositoryWithPendingDraftAsync(store);
            projectListRevision = store.Revision;
            isProjectListLossless = store.IsRecoverySafe;
            lastSavedFingerprint = CurrentStoreFingerprint() == expectedFingerprint ? expectedFingerprint : null;
            savedBaseline = store;
            pendingIdentity = null;
            pendingAdditionEvidence.Clear();
            pendingExtractedProject = null;
            HasUnsavedProjectList = lastSavedFingerprint is null;
        }
        catch
        {
            HasUnsavedProjectList = true;
            if (repository is JsonProjectRepository json && json.HasInterruptedPublication)
                ProjectListPublicationStatus = "中断された一覧保存があります。「中断された一覧保存を確認」を使ってください。";
            throw;
        }
    }

    private async Task ExecuteWithBusyAsync(string operationName, Func<Task> action)
    {
        if (retainedRecoverySnapshot is not null && operationName is "Add batch" or "Remove" or "Open" or "Retry project list save" or "Assign project identity" or "Associate legacy metadata" or "Recover project identity" or "Recover project list")
        {
            AssetStatus = "回復前と画面の一覧を退避し、確認するまで一覧変更はできません。";
            return;
        }
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        logger.Info($"{operationName}開始。");
        logger.Flush();
        try
        {
            await action().ConfigureAwait(true);
            logger.Info($"{operationName}完了。");
            logger.Flush();
        }
        catch (OperationCanceledException)
        {
            logger.Info($"{operationName}キャンセル。");
            logger.Flush();
        }
        catch (ProjectListConflictException ex)
        {
            logger.Error(ex, "Project list changed externally.");
            MessageBox.Show("一覧が別の操作で変更されました。未保存の変更は画面に残っています。新しい一覧を確認し、変更を再適用してください。", "YMM Project Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (ProjectListBusyException ex)
        {
            logger.Error(ex, "Project list is locked by another process.");
            MessageBox.Show("別のプロセスが一覧を保存中です。未保存の変更は画面に残っています。処理が終わってから再試行してください。", "YMM Project Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (InvalidDataException ex) when (operationName is "Recover project identity" or "Recover project list")
        {
            logger.Error(ex, "Project list recovery was rejected.");
            MessageBox.Show($"競合を自動回復できません。未保存の一覧は保持しています。\n{ex.Message}", "一覧の競合回復", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (InvalidDataException ex) when (HasUnsavedProjectList && !isProjectListLossless)
        {
            logger.Error(ex, "Project list contains unsupported data.");
            MessageBox.Show("一覧に安全に保存できない項目があるため、元ファイルを保護して保存を中止しました。未保存の変更は画面に残っています。", "YMM Project Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception) when (operationName == GitHubBrowserLoginOperation)
        {
            GitStatus = "GitHub認証の結果を確認できません。再試行の前に、使用する資格情報と対象リポジトリへのアクセスを確認してください。";
            logger.Info("GitHub browser login: unexpected outcome.");
            logger.Flush();
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"{operationName}失敗。");
            logger.Flush();
            MessageBox.Show("処理中にエラーが発生しました。一覧に未保存の変更がある場合は「一覧の保存を再試行」を使ってください。ログを確認してください。", "YMM Project Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool TryGetOpenableProjectPath(string sourcePath, out string fullPath, out string reason)
    {
        fullPath = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            reason = "プロジェクトパスが空です。";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(sourcePath);
        }
        catch (Exception)
        {
            reason = "プロジェクトパスが不正です。";
            return false;
        }

        if (!fullPath.EndsWith(".ymmp", StringComparison.OrdinalIgnoreCase))
        {
            reason = "拡張子が .ymmp のファイルのみ開けます。";
            return false;
        }

        if (!File.Exists(fullPath))
        {
            reason = "プロジェクトファイルが見つかりません。";
            return false;
        }

        return true;
    }

    //CSJ,method=GetActiveWindow,アクティブウィンドウを取得する
    private static Window? GetActiveWindow()
    {
        return System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive);
    }
}
