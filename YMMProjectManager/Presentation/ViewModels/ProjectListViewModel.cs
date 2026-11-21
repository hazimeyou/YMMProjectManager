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
using YMMProjectManager.Infrastructure.Generations;
using YMMProjectManager.Infrastructure.Git;
using YMMProjectManager.Infrastructure.Output;
using YMMProjectManager.Infrastructure.Packaging;
using YMMProjectManager.Infrastructure.Thumbnails;
using YMMProjectManager.Presentation.Commands;
using YMMProjectManager.Presentation.Generation;
using YMMProjectManager.Presentation.Relink;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectListViewModel : ViewModelBase, ITimelineToolViewModel
{
    private readonly FileLogger logger;
    private readonly IProjectRepository repository;
    private readonly SeekPreviewThumbnailGenerator seekPreviewThumbnailGenerator;
    private readonly IProjectGenerationService generationService;
    private readonly YmmpxLibBundleService bundleService;
    private List<ProjectFolder> folders = [];
    private ProjectEntry? selectedProject;
    private bool isBusy;
    private bool isInitialized;
    private ProjectListRevision? projectListRevision;
    private PendingIdentity? pendingIdentity;
    private string? lastSavedFingerprint;
    private ProjectStore? savedBaseline;
    private ProjectStore? retainedRecoverySnapshot;
    private ProjectStore? retainedScreenSnapshot;
    private readonly Dictionary<string, (Guid Id, string Hash)> pendingAdditionEvidence = new(StringComparer.OrdinalIgnoreCase);
    private bool isProjectListLossless;
    private bool hasUnsavedProjectList;
    private string bundleStatus = string.Empty;
    private double bundleProgress;
    private string gitStatus = string.Empty;
    private string gitPath = string.Empty;
    private string gitCommitMessage = string.Empty;
    private string gitBranch = string.Empty;
    private string gitRemote = string.Empty;
    private string gitCloneName = string.Empty;
    private string assetPath = string.Empty;
    private string assetStatus = string.Empty;
    private string assetUrl = string.Empty;
    private IReadOnlyList<AssetMetadata>? inspectedAssets;
    private CancellationTokenSource? toolCancellation;

    public ObservableCollection<ProjectEntry> Projects { get; } = [];

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

    public string BundleStatus
    {
        get => bundleStatus;
        set => SetProperty(ref bundleStatus, value);
    }

    public double BundleProgress
    {
        get => bundleProgress;
        set => SetProperty(ref bundleProgress, value);
    }

    public bool HasUnsavedProjectList
    {
        get => hasUnsavedProjectList;
        private set
        {
            if (SetProperty(ref hasUnsavedProjectList, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public ICommand RetryProjectListSaveCommand { get; }
    public ICommand AssignLegacyProjectIdCommand { get; }
    public ICommand AssociateLegacyMetadataCommand { get; }
    public ICommand RecoverIdentityCommand { get; }
    public ICommand RecoverProjectListCommand { get; }
    public ICommand ExportRecoverySnapshotCommand { get; }
    public ICommand ExportRecoveryScreenCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand GenerateThumbnailsFastCommand { get; }
    public ICommand RunSeekProbeCommand { get; }
    public ICommand OpenRelinkWindowCommand { get; }
    public ICommand OpenSelectedProjectRelinkCommand { get; }
    public ICommand PackageSelectedProjectCommand { get; }
    public ICommand PackageOpenedProjectCommand { get; }
    public ICommand ExtractBundleCommand { get; }
    public ICommand RefreshGitStatusCommand { get; }
    public ICommand InitializeGitCommand { get; }
    public ICommand StageGitPathCommand { get; }
    public ICommand UnstageGitPathCommand { get; }
    public ICommand CommitGitCommand { get; }
    public ICommand CreateGitBranchCommand { get; }
    public ICommand SwitchGitBranchCommand { get; }
    public ICommand FetchGitCommand { get; }
    public ICommand PullGitCommand { get; }
    public ICommand PushGitCommand { get; }
    public ICommand GitHistoryCommand { get; }
    public ICommand GitOriginCommand { get; }
    public ICommand GitCloneCommand { get; }
    public ICommand FindAssetCandidatesCommand { get; }
    public ICommand InspectAssetCommand { get; }
    public ICommand ExportAssetMetadataCommand { get; }
    public ICommand UpdateAssetMetadataCommand { get; }
    public ICommand VerifyAssetMetadataCommand { get; }
    public ICommand CancelToolCommand { get; }
    public string GitStatus { get => gitStatus; private set => SetProperty(ref gitStatus, value); }
    public string GitPath { get => gitPath; set => SetProperty(ref gitPath, value); }
    public string GitCommitMessage { get => gitCommitMessage; set => SetProperty(ref gitCommitMessage, value); }
    public string GitBranch { get => gitBranch; set => SetProperty(ref gitBranch, value); }
    public string GitRemote { get => gitRemote; set => SetProperty(ref gitRemote, value); }
    public string GitCloneName { get => gitCloneName; set => SetProperty(ref gitCloneName, value); }
    public string AssetPath { get => assetPath; set { if (SetProperty(ref assetPath, value)) inspectedAssets = null; } }
    public string AssetUrl { get => assetUrl; set { if (SetProperty(ref assetUrl, value)) inspectedAssets = null; } }
    public string AssetStatus { get => assetStatus; private set => SetProperty(ref assetStatus, value); }

    public ProjectListViewModel()
        : this(CreateLogger(), null)
    {
    }

    internal ProjectListViewModel(FileLogger logger, IProjectRepository? repository)
    {
        // この ViewModel には、安定しているサムネイル経路だけを持たせる。
        this.logger = logger;
        this.repository = repository ?? new JsonProjectRepository(logger);
        var currentPreviewCaptureService = new CurrentPreviewCaptureService(logger);
        seekPreviewThumbnailGenerator = new SeekPreviewThumbnailGenerator(logger, currentPreviewCaptureService);
        generationService = new ProjectGenerationService(logger);
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
        ReviewProjectCleanupCommand = new AsyncRelayCommand(ReviewProjectCleanupAsync, () => !IsBusy && SelectedProject is not null);
        CollectAssetToPoolCommand = new AsyncRelayCommand(CollectAssetToPoolAsync, () => !IsBusy && SelectedProject is not null);
        InspectGitHubAccessCommand = new AsyncRelayCommand(InspectGitHubAccessAsync, () => !IsBusy && SelectedProject is not null);
        RefreshGitStatusCommand = new AsyncRelayCommand(() => ShowGitStatusAsync(false), () => !IsBusy && SelectedProject is not null);
        InitializeGitCommand = new AsyncRelayCommand(() => ShowGitStatusAsync(true), () => !IsBusy && SelectedProject is not null);
        StageGitPathCommand = new AsyncRelayCommand(() => ChangeGitAsync("stage"), () => !IsBusy && SelectedProject is not null);
        UnstageGitPathCommand = new AsyncRelayCommand(() => ChangeGitAsync("unstage"), () => !IsBusy && SelectedProject is not null);
        CommitGitCommand = new AsyncRelayCommand(() => ChangeGitAsync("commit"), () => !IsBusy && SelectedProject is not null);
        CreateGitBranchCommand = new AsyncRelayCommand(() => ChangeGitAsync("branch-create"), () => !IsBusy && SelectedProject is not null);
        SwitchGitBranchCommand = new AsyncRelayCommand(() => ChangeGitAsync("branch-switch"), () => !IsBusy && SelectedProject is not null);
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

    private async Task ExecuteToolAsync(string name, Func<CancellationToken, Task> action)
    {
        await ExecuteWithBusyAsync(name, async () =>
        {
            using var cancellation = new CancellationTokenSource();
            toolCancellation = cancellation;
            CommandManager.InvalidateRequerySuggested();
            try { await action(cancellation.Token); }
            catch (OperationCanceledException ex)
            {
                var recovery = ex.Data["CloneRecoveryPath"] as string;
                var message = "中止しました。ディスクやリモートの状態を確認してください。";
                if (recovery is not null) message += " 残存 clone: " + recovery;
                if (ex.Data["YmmpxRetainedStagingPath"] is string staging) message += $" 展開一時フォルダー: {staging}";
                if (name == InspectListPublicationOperation)
                    ProjectListPublicationStatus = "中止しました。保存記録を確認するには、もう一度実行してください。";
                else if (name == PullWorkflowOperation)
                    GitStatus = "Pull を中止しました。ブランチ、index、作業ファイルの更新は開始していません。";
                else if (name.StartsWith("Asset", StringComparison.Ordinal)) AssetStatus = message;
                else if (name.StartsWith("Bundle", StringComparison.Ordinal)) BundleStatus = message;
                else GitStatus = message;
                throw;
            }
            finally { toolCancellation = null; CommandManager.InvalidateRequerySuggested(); }
        });
    }

    private async Task ShowGitDetailsAsync(bool remote)
    {
        var selected = SelectedProject;
        if (selected is null) return;
        await ExecuteToolAsync("Git", async token =>
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(selected.FullPath)) ?? throw new InvalidOperationException("プロジェクトフォルダーがありません。");
            var git = new GitRepository(directory);
            var result = remote ? await git.OriginAsync(token) : await git.HistoryAsync(token);
            if (!ReferenceEquals(selected, SelectedProject)) return;
            if (!result.Success) { GitStatus = "情報を取得できません。origin またはコミット履歴を確認してください。"; return; }
            var details = result.StandardOutput.Replace('\0', ' ');
            var github = remote && Uri.TryCreate(details, UriKind.Absolute, out var uri) &&
                         uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
            GitStatus = github ? "GitHub origin: " + details : details;
        });
    }

    private async Task CloneGitAsync()
    {
        var remote = GitRemote;
        var name = GitCloneName;
        if (string.IsNullOrWhiteSpace(remote) || string.IsNullOrWhiteSpace(name) || name is "." or ".." || Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        { GitStatus = "HTTPS またはローカルの URL と、フォルダー名を入力してください。"; return; }
        var inspected = GitRepository.InspectCloneRemote(remote);
        if (!inspected.Success) { GitStatus = DescribeRemoteFailure(inspected); return; }
        var dialog = new OpenFolderDialog { Title = "clone 先の親フォルダーを選択" };
        if (dialog.ShowDialog() != true) return;
        var destination = Path.Combine(dialog.FolderName, name);
        var authentication = AuthenticationFor(remote);
        if (!ConfirmGitTrust("Clone", destination, inspected.StandardOutput + AuthenticationNotice(authentication))) return;
        await ExecuteToolAsync("Git clone", async token =>
        {
            GitStatus = "Clone を実行中です。";
            try
            {
                var result = await GitRepository.CloneTrustedAsync(remote, destination, authentication, token);
                GitStatus = result.Success ? $"Clone が完了しました。{destination}" : DescribeRemoteFailure(result);
            }
            catch (ArgumentException)
            {
                GitStatus = "接続先または保存先の形式を確認してください。資格情報は URL に入力できません。";
            }
        });
    }

    private Task RunTrustedRemoteAsync(GitRemoteOperation operation) => ExecuteToolAsync($"Git {operation}", async token =>
    {
        var project = SelectedProject;
        if (project is null) return;
        var located = await LocateProjectGitRootAsync(project, token);
        if (located is null) { GitStatus = "このプロジェクトの Git ルートを確認できません。"; return; }
        var (git, directory) = located.Value;
        var inspected = await git.InspectRemoteAsync(operation, token);
        if (!inspected.Success) { GitStatus = DescribeRemoteFailure(inspected); return; }
        var push = inspected.PushTarget;
        var fetch = inspected.FetchTarget;
        if (push is null && fetch is null) throw new InvalidDataException("承認対象のリポジトリと接続先を確認できません。");
        var details = push is null ? fetch!.Endpoint :
            $"{push.Endpoint}\n対象 {push.BranchRef}\nコミット {push.ObjectId}";
        if (push is not null && Path.IsPathFullyQualified(push.Endpoint))
            details += "\nローカルの送信先では受信側Gitの設定と受信hookが適用されます。管理している送信先だけを承認してください。";
        var authentication = AuthenticationFor(push?.Endpoint ?? fetch!.Endpoint);
        if (!ReferenceEquals(project, SelectedProject) || !ConfirmGitTrust(operation.ToString(), directory,
                details + AuthenticationNotice(authentication))) return;
        token.ThrowIfCancellationRequested();
        GitStatus = $"{operation} を実行中です。";
        if (push is not null)
        {
            try { await RegisterKnownPublicationAssetsAsync(git, project, token); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException)
            {
                GitStatus = "プロジェクトまたは素材メタデータを確認できないため、Pushを中止しました。";
                return;
            }
        }
        var result = push is null
            ? await git.FetchTrustedAsync(fetch!, authentication, token)
            : await git.PushTrustedAsync(push, authentication, token);
        GitStatus = result.Success ? $"{operation} が完了しました。" : DescribeRemoteFailure(result);
    });

    private static bool ConfirmGitTrust(string operation, string directory, string endpoint) =>
        MessageBox.Show($"{operation}\n{directory}\n{endpoint}\n\nインストール済みの Git、システム・利用者・リポジトリの Git 設定、認証ヘルパーなどを信頼する場合に限り続行してください。",
            "Git 操作の確認", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private static string DescribeRemoteFailure(GitResult result)
    {
        var action = result.Failure switch
        {
            GitFailure.Authentication => "認証の有効期限、使用するアカウント、認証ヘルパーを確認してください。",
            GitFailure.Permission => "接続先と、対象リポジトリへの権限を確認してください。",
            GitFailure.Network => "接続先、ネットワーク、TLS 設定を確認してください。",
            GitFailure.RemoteRedirect => "接続先がリダイレクトを要求したため停止しました。正式なURLを確認し、接続先を承認し直してください。",
            GitFailure.Divergence => "送信先の履歴が変更されています。Fetch してブランチの状態を確認してください。",
            GitFailure.Timeout => "制限時間に達しました。接続状況と操作結果を確認してください。",
            GitFailure.PathTooLong => "より短い親フォルダーを選んでください。",
            GitFailure.MediaPublication => "素材・パッケージ・ローカル専用の回復記録が含まれるか、送信内容を検証できないため拒否しました。過去のコミットも確認してください。",
            GitFailure.UnsafeOperation => "接続先・ブランチ・リポジトリの状態または Git 設定を確認し、再度承認してください。",
            _ => "接続先、Git 設定またはブランチを確認してください。",
        };
        var message = $"Git 操作を完了できませんでした。{action}";
        return result.RecoveryPath is { } path ? $"{message}\n残存フォルダー {path}" : message;
    }

    private async Task FindAssetCandidatesAsync()
    {
        var selected = SelectedProject;
        if (selected is null) return;
        await ExecuteToolAsync("Asset candidates", async token =>
        {
            var source = selected.FullPath;
            var expected = selected.ProjectId is Guid id && selected.MetadataAssociation is { } association
                ? (await ResolveMetadataBranchAsync(id, association, token)).Selected.Manifest.Assets : null;
            var references = await AssetInventory.ScanProjectAsync(source, expected, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(selected, SelectedProject) || selected.FullPath != source) return;
            var rows = references.Select(reference =>
            {
                var status = reference.Status switch
                {
                    AssetReferenceStatus.Present => "ファイルあり",
                    AssetReferenceStatus.Missing => "不足",
                    AssetReferenceStatus.Changed => "変更あり",
                    AssetReferenceStatus.External => "プロジェクト外",
                    _ => "検査できません",
                };
                var actual = reference.Actual is { } asset ? $" / {asset.Size} bytes / SHA-256 {asset.Sha256}" : "";
                var prior = reference.Expected is { } known ? $" / 期待 {known.Size} bytes / SHA-256 {known.Sha256}" : "";
                var external = reference.Expected?.ExternalLocation is { } location ? $" / 保管先 {location.StorageName}/{location.RelativePath}" : "";
                return $"{reference.Path}  {status}{actual}{prior}{external}";
            });
            AssetStatus = references.Count == 0 ? "FilePath 候補はありません。" :
                "FilePath 候補（素材とは限りません）\n" + string.Join("\n", rows);
        });
    }

    private async Task OpenSelectedProjectRelinkAsync()
    {
        var selected = SelectedProject;
        if (selected is null) return;
        await ExecuteWithBusyAsync("Selected project relink", async () =>
        {
            if (!TryGetOpenableProjectPath(selected.FullPath, out var projectPath, out var reason))
            {
                MessageBox.Show(reason, "再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (TimelineContextService.Timeline is not null)
            {
                MessageBox.Show("タイムラインが開いている間はファイル側の再リンクを行いません。タイムライン用の再リンクを使用してください。", "再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Func<CancellationToken, Task<AssetRevisionSeries.BranchSelection>>? resolve = null;
            string? seriesDirectory = null;
            if (selected.ProjectId is Guid id && selected.MetadataAssociation is { } association)
            {
                if (selected.FileIdentity is { } observed && !observed.IsSameFile(await ProjectFileEvidence.ReadAsync(projectPath)))
                    throw new InvalidDataException("登録時と異なるファイルです。「識別情報を照合」から確認してください。");
                seriesDirectory = association.Directory;
                resolve = token => ResolveMetadataBranchAsync(id, association, token);
            }
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            OpenRelinkWindowDialog(new RelinkMainWindow(projectPath, logger, resolve, seriesDirectory)
            {
                ExpectedSourceIdentity = selected.FileIdentity,
                TransferAssociationAsync = selected.MetadataAssociation is { } metadata
                    ? (receipt, token) => ApplyRelinkOutputAsync(selected, metadata, receipt, token) : null,
                RegisterDerivedAsync = selected.MetadataAssociation is { } sourceMetadata
                    ? (receipt, token) => dispatcher.InvokeAsync(() => ApplyDerivedRelinkAsync(selected, sourceMetadata, receipt, token)).Task.Unwrap()
                    : null,
            });
        });
    }

    private async Task InspectAssetAsync()
    {
        var selected = SelectedProject;
        if (selected is null) return;
        await ExecuteToolAsync("Asset inspection", async token =>
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(selected.FullPath)) ?? throw new InvalidOperationException("プロジェクトフォルダーがありません。");
            var requestedPath = AssetPath;
            var requestedUrl = AssetUrl;
            inspectedAssets = null;
            inspectedExternalRoot = null;
            var paths = requestedPath.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 0 || paths.Length > 1000) { AssetStatus = "素材パスを1〜1000件入力してください。"; return; }
            if (paths.Length != 1 && !string.IsNullOrWhiteSpace(requestedUrl)) { AssetStatus = "URL は素材を1件指定したときだけ入力できます。"; return; }
            var assets = new List<AssetMetadata>(paths.Length);
            foreach (var path in paths)
            {
                try { assets.Add(await AssetInventory.InspectAsync(directory, path, string.IsNullOrWhiteSpace(requestedUrl) ? null : requestedUrl, token)); }
                catch (FileNotFoundException) { AssetStatus = $"素材が見つかりません: {path}"; return; }
                catch (DirectoryNotFoundException) { AssetStatus = $"素材が見つかりません: {path}"; return; }
            }
            if (!ReferenceEquals(selected, SelectedProject) || AssetPath != requestedPath || AssetUrl != requestedUrl) return;
            inspectedAssets = assets;
            AssetStatus = string.Join("\n", assets.Select(asset => $"{asset.RelativePath}: {asset.Size} bytes SHA-256: {asset.Sha256}"));
        });
    }

    private Task ExportAssetMetadataAsync() => SaveAssetMetadataAsync(false);

    private async Task SaveAssetMetadataAsync(bool update)
    {
        var selected = SelectedProject;
        var assets = inspectedAssets;
        var externalRoot = inspectedExternalRoot;
        if (selected is null || assets is null) { AssetStatus = "先に素材を検査してください。"; return; }
        var association = update ? selected.MetadataAssociation : null;
        var source = selected.FullPath;
        string? parentPath = null;
        if (update && association is null)
        {
            var existing = new OpenFileDialog { Filter = "JSON (*.json)|*.json", Title = "更新元の素材メタデータを選択", CheckFileExists = true };
            if (existing.ShowDialog() != true) return;
            parentPath = existing.FileName;
        }
        var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", Title = "素材メタデータを新しいファイルに保存", OverwritePrompt = false };
        if (association is null && dialog.ShowDialog() != true) return;
        await ExecuteToolAsync("Asset metadata export", async token =>
        {
            if (HasUnsavedProjectList) { AssetStatus = "プロジェクト一覧に未保存の変更があります。先に一覧を保存してください。"; return; }
            if (!ReferenceEquals(selected, SelectedProject) || !ReferenceEquals(assets, inspectedAssets)) { AssetStatus = "選択が変わりました。再検査してください。"; return; }
            var directory = Path.GetDirectoryName(Path.GetFullPath(source)) ?? throw new InvalidOperationException("プロジェクトフォルダーがありません。");
            foreach (var asset in assets)
                if (await VerifyInspectedAssetAsync(directory, externalRoot, asset, token) != AssetVerification.Valid) { AssetStatus = $"素材が変更されたか見つかりません: {asset.RelativePath}"; return; }
            var verifiedAtUtc = DateTimeOffset.UtcNow;
            if (!ReferenceEquals(selected, SelectedProject) || !ReferenceEquals(assets, inspectedAssets)) { AssetStatus = "選択が変わりました。再検査してください。"; return; }
            var managed = association is not null && selected.ProjectId is Guid id ? await ResolveMetadataBranchAsync(id, association, token) : null;
            if (association is not null && (managed is null || !managed.Graph.Heads.Contains(managed.Selected.Receipt)))
                throw new InvalidDataException("現在の head を選択し直してから版を追加してください。");
            var parent = managed?.Selected.Manifest ?? (parentPath is null ? null : await AssetManifestStore.LoadAsync(parentPath, token));
            if (!ReferenceEquals(selected, SelectedProject) || !ReferenceEquals(assets, inspectedAssets)) { AssetStatus = "選択が変わりました。再検査してください。"; return; }
            if (parent is not null && MessageBox.Show($"更新元のプロジェクト名: {parent.ProjectFileName}\n選択中: {selected.FullPath}\n同名でも別プロジェクトの可能性があります。同一プロジェクトの版として作成しますか？", "素材メタデータ", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var changes = parent is null ? [] : AssetManifestStore.FindChangedPaths(parent, assets);
            if (changes.Count > 0)
            {
                if (changes.Count > 10) { AssetStatus = "変更が10件を超えています。すべてを確認できるよう、10件以下に分けて更新してください。"; return; }
                var details = changes.Take(10).Select(path =>
                {
                    var normalized = Path.GetFullPath(path, Path.GetTempPath());
                    var oldAsset = parent!.Assets.First(item => string.Equals(Path.GetFullPath(item.RelativePath, Path.GetTempPath()), normalized, StringComparison.OrdinalIgnoreCase));
                    var newAsset = assets.First(item => string.Equals(Path.GetFullPath(item.RelativePath, Path.GetTempPath()), normalized, StringComparison.OrdinalIgnoreCase));
                    return $"{path}\n以前: {oldAsset.Size} bytes / {oldAsset.Sha256}\n今回: {newAsset.Size} bytes / {newAsset.Sha256}";
                });
                if (MessageBox.Show($"サイズまたは SHA-256 が以前と異なります:\n{string.Join("\n", details)}\n変更 {changes.Count} 件を新しい版に記録しますか？", "素材メタデータ", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            }
            if (!ReferenceEquals(selected, SelectedProject) || !ReferenceEquals(assets, inspectedAssets) ||
                selected.FullPath != source || HasUnsavedProjectList || (association is not null && selected.MetadataAssociation != association))
                throw new InvalidDataException("素材またはプロジェクトの選択が変更されました。");
            foreach (var asset in assets)
                if (await VerifyInspectedAssetAsync(directory, externalRoot, asset, token) != AssetVerification.Valid)
                    throw new InvalidDataException($"確認後に素材が変更されました。{asset.RelativePath}");
            var manifest = AssetManifestStore.Create(source, assets, verifiedAtUtc, parent, confirmedProjectAssociation: parent is not null, confirmedHashChanges: changes.Count > 0, projectId: selected.ProjectId);
            if (association is not null && managed is not null)
            {
                var receipt = await AssetRevisionSeries.AppendToBranchAsync(association.Directory, managed.Graph.Root,
                    managed.Graph.Heads, managed.Selected.Receipt, manifest, token);
                AssetStatus = $"履歴に版を追加しました。{receipt.RevisionId}\n使用する版は「履歴の版を選択」から変更できます。";
                return;
            }
            await AssetManifestStore.SaveNewAsync(dialog.FileName, manifest, token);
            AssetStatus = selected.ProjectId is null
                ? "素材メタデータを保存しました。旧プロジェクトとの永続 ID 関連付けは未設定です。"
                : "素材メタデータを新しいファイルに保存しました。元のメタデータは変更していません。";
        });
    }

    private async Task VerifyAssetMetadataAsync()
    {
        var selected = SelectedProject;
        if (selected is null) return;
        var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json", Title = "素材メタデータを選択", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        await ExecuteToolAsync("Asset metadata verification", async token =>
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(selected.FullPath)) ?? throw new InvalidOperationException("プロジェクトフォルダーがありません。");
            IReadOnlyList<AssetMetadata> assets;
            var associationVerified = false;
            try
            {
                var manifest = await AssetManifestStore.LoadAsync(dialog.FileName, token);
                if (selected.ProjectId is Guid projectId && manifest.ProjectId != projectId)
                { AssetStatus = "メタデータのプロジェクト ID が一致しません。"; return; }
                associationVerified = selected.ProjectId is not null;
                if (selected.ProjectId is null && !string.Equals(manifest.ProjectFileName, Path.GetFileName(selected.FullPath), StringComparison.OrdinalIgnoreCase))
                { AssetStatus = "メタデータのプロジェクト名が一致しません。"; return; }
                assets = manifest.Assets;
            }
            catch (JsonException)
            {
                // R2 exported a bare array; retain read compatibility without rewriting it.
                assets = await AssetInventory.LoadAsync(dialog.FileName, token);
            }
            var results = new List<string>();
            foreach (var asset in assets)
                results.Add($"{asset.RelativePath}: {await AssetInventory.VerifyAsync(directory, asset, token)}");
            if (!ReferenceEquals(selected, SelectedProject)) return;
            AssetStatus = (results.Count == 0 ? "メタデータに素材がありません。" : string.Join("\n", results)) + (associationVerified ? $"\nプロジェクト ID 一致: {selected.ProjectId}" : "\nプロジェクトとの ID 関連付けは未確認です。");
        });
    }

    private async Task ChangeGitAsync(string operation)
    {
        var selected = SelectedProject;
        if (selected is null) return;
        var requestedPath = GitPath;
        var requestedMessage = GitCommitMessage;
        var requestedBranch = GitBranch;
        await ExecuteToolAsync("Git", async token =>
        {
            var located = await LocateProjectGitRootAsync(selected, token);
            if (located is null || !ReferenceEquals(selected, SelectedProject)) { GitStatus = "Gitリポジトリを確認できません。"; return; }
            var (git, directory) = located.Value;
            if ((operation == "stage" || operation == "unstage") && string.IsNullOrWhiteSpace(requestedPath)) { GitStatus = "Gitルートからの相対パスを指定してください。"; return; }
            if (operation == "commit" && string.IsNullOrWhiteSpace(requestedMessage)) { GitStatus = "コミットメッセージを入力してください。"; return; }
            if ((operation == "branch-create" || operation == "branch-switch") && string.IsNullOrWhiteSpace(requestedBranch)) { GitStatus = "ブランチ名を入力してください。"; return; }
            if (operation == "branch-switch")
            {
                if (MessageBox.Show("ブランチ切替時に、このリポジトリで設定された Git フィルターなどの外部プログラムが実行される場合があります。設定を信頼できる場合のみ続行してください。", "Git", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            }
            if (operation is "commit" or "stage")
            {
                try { await RegisterKnownPublicationAssetsAsync(git, selected, token); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException)
                {
                    GitStatus = "プロジェクトまたは素材メタデータを確認できないため、Git操作を中止しました。";
                    return;
                }
            }
            GitCommitTarget? commitTarget = null;
            if (operation == "commit")
            {
                var inspected = await git.InspectCommitAsync(token);
                if (!inspected.Success || inspected.CommitTarget is not { } target) { GitStatus = DescribeCommitFailure(inspected); return; }
                if (!ReferenceEquals(selected, SelectedProject) || !ConfirmGitCommit(target, requestedMessage)) return;
                commitTarget = target;
            }
            if (!ReferenceEquals(selected, SelectedProject)) return;
            var result = operation switch
            {
                "stage" => await git.StageAsync([requestedPath], token),
                "unstage" => await git.UnstageAsync([requestedPath], token),
                "commit" => await git.CommitAsync(commitTarget!, requestedMessage, token),
                "branch-create" => await git.CreateBranchAsync(requestedBranch, token),
                "branch-switch" => await git.SwitchBranchAsync(requestedBranch, token),
                _ => throw new InvalidOperationException("Unknown Git operation."),
            };
            if (!ReferenceEquals(selected, SelectedProject)) return;
            if (!result.Success) { GitStatus = DescribeCommitFailure(result); return; }
            GitStatus = operation == "commit" ? $"コミットしました。\n{result.StandardOutput.Trim()}" : "Git 操作が完了しました。";
            if (operation == "commit" && GitCommitMessage == requestedMessage) GitCommitMessage = "";
            try
            {
                var status = await git.StatusAsync(token);
                if (status.Success && ReferenceEquals(selected, SelectedProject)) UpdateGitChanges(status.StandardOutput);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The mutation already succeeded. Cancellation affects only the display refresh.
            }
        });
    }

    private async Task ShowGitStatusAsync(bool initialize)
    {
        var selected = SelectedProject;
        if (selected is null) return;
        await ExecuteToolAsync("Git", async token =>
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(selected.FullPath));
            if (directory is null || !Directory.Exists(directory))
            {
                GitStatus = "プロジェクトのフォルダーが見つかりません。";
                return;
            }
            var git = new GitRepository(directory);
            var detected = await git.DetectAsync(token);
            if (!ReferenceEquals(selected, SelectedProject)) return;
            if (!detected.Success && initialize)
            {
                if (!detected.StandardError.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
                {
                    GitStatus = "Git リポジトリの状態を確認できないため、初期化を中止しました。";
                    return;
                }
                if (MessageBox.Show("このフォルダーに Git リポジトリを初期化しますか？", "Git", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                var recheck = await git.DetectAsync(token);
                if (recheck.Success || !recheck.StandardError.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
                {
                    GitStatus = "Git の状態が変化したため、初期化を中止しました。";
                    return;
                }
                var result = await git.InitializeAsync(token);
                if (!result.Success) { GitStatus = "Git の初期化に失敗しました。"; return; }
                detected = await git.DetectAsync(token);
            }
            if (!detected.Success) { GitStatus = "Git リポジトリが見つかりません。"; return; }
            var status = await git.StatusAsync(token);
            var branches = await git.BranchesAsync(token);
            if (!ReferenceEquals(selected, SelectedProject)) return;
            if (status.Success) UpdateGitChanges(status.StandardOutput);
            GitStatus = status.Success && branches.Success
                ? $"Git: {detected.StandardOutput.Trim()}\n{branches.StandardOutput.Trim()}\n{(GitChanges.Count == 0 ? "変更なし" : $"変更 {GitChanges.Count} 件")}"
                : "Git の状態を取得できませんでした。";
        });
    }

    public void SetTimelineToolInfo(TimelineToolInfo info)
    {
        TimelineContextService.Info = info;
        var timeline = info.Timeline;
        logger.Info($"SetTimelineToolInfo called. timeline={(timeline is null ? "null" : "available")}, currentFrame={timeline?.CurrentFrame}, length={timeline?.Length}");
        CommandManager.InvalidateRequerySuggested();
    }

    public async Task InitializeAsync()
    {
        if (isInitialized)
        {
            return;
        }

        await ExecuteWithBusyAsync("Load", async () =>
        {
            logger.Info("Load start.");
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

    public async Task AddProjectsAsync(IEnumerable<string>? explicitPaths = null)
    {
        await ExecuteWithBusyAsync("Add batch", async () =>
        {
            if (!isInitialized) throw new InvalidOperationException("プロジェクト一覧が読み込まれていません。");
            var paths = explicitPaths?.ToArray();
            if (paths is null)
            {
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

            var existing = new HashSet<string>(Projects.Select(x => x.FullPath), StringComparer.OrdinalIgnoreCase);
            var addedEntries = new List<ProjectEntry>();
            foreach (var rawPath in paths)
            {
                if (!rawPath.EndsWith(".ymmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

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

    public async Task SaveGenerationAsync()
    {
        var project = SelectedProject;
        if (project is null)
        {
            MessageBox.Show("世代を保存するプロジェクトを選択してください。", "世代管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryGetOpenableProjectPath(project.FullPath, out var projectPath, out var reason))
        {
            MessageBox.Show(reason, "世代管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new ProjectGenerationSaveDialog
        {
            Owner = GetActiveWindow(),
            DisplayName = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm"),
            Memo = string.Empty,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ExecuteWithBusyAsync("GenerationSave", async () =>
        {
            var displayName = string.IsNullOrWhiteSpace(dialog.DisplayName)
                ? DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm")
                : dialog.DisplayName.Trim();
            var memo = string.IsNullOrWhiteSpace(dialog.Memo) ? null : dialog.Memo.Trim();

            var generation = await generationService.CreateGenerationAsync(projectPath, displayName, memo).ConfigureAwait(true);
            logger.Info($"Generation saved. projectPath={projectPath}, generationId={generation.GenerationId}");
            MessageBox.Show($"世代を保存しました。\n{generation.DisplayName}", "世代管理", MessageBoxButton.OK, MessageBoxImage.Information);
        }).ConfigureAwait(true);
    }

    public async Task ShowGenerationListAsync()
    {
        var project = SelectedProject;
        if (project is null)
        {
            MessageBox.Show("世代一覧を表示するプロジェクトを選択してください。", "世代管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryGetOpenableProjectPath(project.FullPath, out var projectPath, out var reason))
        {
            MessageBox.Show(reason, "世代管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var window = new ProjectGenerationListWindow(projectPath, logger)
        {
            Owner = GetActiveWindow(),
        };
        window.ShowDialog();
        await Task.CompletedTask;
    }

    public async Task ShowGenerationDiagnosticsAsync()
    {
        var project = SelectedProject;
        if (project is null)
        {
            MessageBox.Show("世代診断を表示するプロジェクトを選択してください。", "世代管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryGetOpenableProjectPath(project.FullPath, out var projectPath, out var reason))
        {
            MessageBox.Show(reason, "世代管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var window = new ProjectGenerationDiagnosticsWindow(projectPath, logger)
        {
            Owner = GetActiveWindow(),
        };
        window.ShowDialog();
        await Task.CompletedTask;
    }

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

    private async Task OpenAsync()
    {
        if (SelectedProject is null)
        {
            return;
        }

        await ExecuteWithBusyAsync("Open", async () =>
        {
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

    private async Task OpenOpenedProjectRelinkWindowAsync()
    {
        await ExecuteWithBusyAsync("OpenOpenedProjectRelinkWindow", () =>
        {
            var info = TimelineContextService.Info;
            if (info?.Timeline is null)
            {
                MessageBox.Show("開いているPFが見つかりませんでした。", "素材再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
                return Task.CompletedTask;
            }

            var projectPath = YmmProjectPathResolver.TryGetCurrentProjectPath() ?? SelectedProject?.FullPath;
            if (!string.IsNullOrWhiteSpace(projectPath) &&
                !TryGetOpenableProjectPath(projectPath, out projectPath, out _))
            {
                projectPath = null;
            }

            OpenRelinkWindowDialog(new RelinkMainWindow(info, logger, projectPath));
            return Task.CompletedTask;
        }).ConfigureAwait(true);
    }

    private async Task PackageSelectedProjectAsync()
    {
        var path = SelectedProject?.FullPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await PackageProjectAsync(path).ConfigureAwait(true);
    }

    private async Task PackageOpenedProjectAsync()
    {
        var currentProjectPath = YmmProjectPathResolver.TryGetCurrentProjectPath();
        if (string.IsNullOrWhiteSpace(currentProjectPath))
        {
            MessageBox.Show("現在 YMM で開いているプロジェクトを取得できませんでした。", "ymmpx", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await PackageProjectAsync(currentProjectPath).ConfigureAwait(true);
    }

    private Task ExtractBundleAsync() => ExecuteToolAsync("Bundle extraction", async token =>
    {
        var bundle = new OpenFileDialog
        {
            Filter = "YMM同梱ファイル (*.ymmpx)|*.ymmpx",
            Title = "展開する ymmpx を選択",
            CheckFileExists = true,
        };
        if (bundle.ShowDialog() != true) return;

        var parent = new OpenFolderDialog { Title = "新しい展開フォルダーを作成する場所を選択" };
        if (parent.ShowDialog() != true) return;
        var destination = Path.Combine(parent.FolderName, Path.GetFileNameWithoutExtension(bundle.FileName));
        if (MessageBox.Show($"次の場所に新しいフォルダーを作成します。\n{destination}\n\n新しい受け渡し用YMMPXは、同じフォルダーの ymmpm-transfer.json と照合します。従来のYMMPXでは素材名とSHA-256の一致は証明できません。",
            "ymmpx 展開", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        BundleStatus = "ymmpx を展開中...";
        BundleProgress = 0;
        var progress = new Progress<double>(value => BundleProgress = value * 100d);
        var result = await bundleService.ExtractPackageAsync(bundle.FileName, destination, token, progress);
        if (!result.Success)
        {
            BundleStatus = result.ErrorMessage ?? "展開に失敗しました。";
            MessageBox.Show(BundleStatus, "ymmpx", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BundleProgress = 100;
        BundleStatus = $"展開完了 {result.RestoredProjectPath}\n素材参照の更新 {result.ReplacedPathCount} 件";
        BundleStatus += result.TransferEvidenceVerified
            ? $"\n素材名・サイズ・SHA-256 照合済み {result.ExtractedResourceCount} 件"
            : "\n従来形式で展開しました。素材名・SHA-256 の照合記録はありません。";
        if (result.MissingMaterials.Count > 0)
        {
            BundleStatus += $"\n不足・同梱対象外 {result.MissingMaterials.Count} 件";
            MessageBox.Show(BundleStatus + "\n\n" + string.Join("\n", result.MissingMaterials.Take(20)) +
                (result.MissingMaterials.Count > 20 ? "\n（先頭20件）" : ""),
                "YMMPX の不足素材", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        if (isInitialized && !HasUnsavedProjectList && retainedRecoverySnapshot is null &&
            result.RestoredProjectPath is { } restored && result.RestoredProjectIdentity is { } extractedIdentity &&
            MessageBox.Show($"{BundleStatus}\n一覧に新しいプロジェクトとして登録しますか？",
                "ymmpx 展開", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
        {
            try { await RegisterExtractedProjectAsync(restored, extractedIdentity, token); }
            catch
            {
                BundleStatus += "\n一覧への登録に失敗しました。展開済みファイルは保持されています。";
                throw;
            }
        }
    });

    private async Task RegisterExtractedProjectAsync(string restoredYmmpPath, ProjectFileIdentity extractedIdentity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(restoredYmmpPath);
        if (Projects.Any(x => string.Equals(Path.GetFullPath(x.FullPath), fullPath, StringComparison.OrdinalIgnoreCase))) return;
        using var directory = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(fullPath)!, enforceShare: true);
        await using var file = ProjectFileEvidence.OpenRead(fullPath);
        var current = await ProjectFileEvidence.ReadAsync(file, token);
        if (current != extractedIdentity)
            throw new IOException("展開後にPFが変更されたため、一覧に登録できません。");
        if (Projects.Any(project => project.FileIdentity is { } known && known.IsSameFile(current)))
            throw new IOException("同じPFが既に一覧にあります。");
        token.ThrowIfCancellationRequested();
        var id = Guid.NewGuid();
        var entry = new ProjectEntry
        {
            FullPath = fullPath,
            ProjectId = id,
            DisplayName = Path.GetFileNameWithoutExtension(fullPath),
            FileIdentity = current,
        };
        pendingAdditionEvidence[fullPath] = (id, entry.FileIdentity.Sha256);
        entry.LinkedYmmpFiles.Add(new LinkedYmmpFile { FilePath = fullPath, DisplayName = entry.DisplayName,
            Role = YmmpRole.Main, Exists = true, LastCheckedAt = DateTimeOffset.Now });
        Projects.Add(entry);
        SelectedProject = entry;
        await SaveAsync();
        PrepareThumbnailMetadata([entry]);
    }

    private void OpenRelinkWindowDialog(RelinkMainWindow window)
    {
        var owner = System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive);
        if (owner is not null && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        window.ShowDialog();
    }

    private async Task GenerateThumbnailsFastAsync()
    {
        var project = SelectedProject;
        if (project is null)
        {
            return;
        }

        await ExecuteWithBusyAsync("Generate thumbnails (fast)", async () =>
        {
            // 高速サムネイル生成は、YMM 側で開いているタイムライン情報を前提にする。
            var info = TimelineContextService.Info;
            if (info?.Timeline is null)
            {
                MessageBox.Show("Open a project in YMM first.", "YMM Project Manager", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = await seekPreviewThumbnailGenerator
                .GenerateAsync(project.FullPath, info, CancellationToken.None)
                .ConfigureAwait(true);

            if (!result.Success)
            {
                MessageBox.Show(
                    result.Reason,
                    "YMM Project Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // 生成後はキャッシュパスを再通知し、一覧のスクラブサムネイルを更新する。
            UpdateThumbnailMetadata(project);
        }).ConfigureAwait(true);
    }

    private async Task RunSeekProbeAsync()
    {
        await ExecuteWithBusyAsync("SeekProbe", async () =>
        {
            var timeline = TimelineContextService.Timeline;
            if (timeline is null)
            {
                MessageBox.Show("Open a project in YMM first.", "YMM Project Manager", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var outputDirectory = Path.Combine(Path.GetTempPath(), "YMMProjectManager", "seek-probe");
            Directory.CreateDirectory(outputDirectory);

            // 代表フレームを複数試し、YMM のタイムライン操作が安定しているかを確認する。
            var probeFrames = CreateSeekProbeFrames(timeline);
            var seekAdapter = new YmmTimelineSeekAdapter();
            var resultPaths = new List<string>(probeFrames.Count);
            var failedCount = 0;

            foreach (var frame in probeFrames)
            {
                try
                {
                    var path = await seekAdapter.WriteSeekProbeAsync(timeline, frame, outputDirectory, CancellationToken.None).ConfigureAwait(true);
                    resultPaths.Add(path);

                    var json = await File.ReadAllTextAsync(path).ConfigureAwait(true);
                    var result = System.Text.Json.JsonSerializer.Deserialize<SeekResult>(json);
                    if (result is not null && !result.Success)
                    {
                        failedCount++;
                    }

                    logger.Info($"Seek probe saved. frame={frame}, path={path}");
                }
                catch (Exception ex)
                {
                    failedCount++;
                    logger.Error(ex, $"Seek probe failed unexpectedly. frame={frame}");
                }
            }

            var message = resultPaths.Count == 0
                ? $"Seek probe produced no result files.\nOutput: {outputDirectory}"
                : $"Seek probe completed.\nFrames: {string.Join(", ", probeFrames)}\nSaved: {resultPaths.Count}\nFailed: {failedCount}\nOutput: {outputDirectory}";

            MessageBox.Show(
                message,
                "シーク確認",
                MessageBoxButton.OK,
                failedCount == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }).ConfigureAwait(true);
    }

    public async Task RetryProjectListSaveAsync()
    {
        if (!HasUnsavedProjectList) return;
        await ExecuteWithBusyAsync("Retry project list save", SaveAsync).ConfigureAwait(true);
    }

    private async Task AssignLegacyProjectIdAsync()
    {
        var project = SelectedProject;
        if (project is null || project.ProjectId is not null || HasUnsavedProjectList) return;
        await ExecuteWithBusyAsync("Assign project identity", async () =>
        {
            if (!isProjectListLossless) { AssetStatus = "一覧に安全に保存できない項目があります。ID の設定を中止しました。"; return; }
            if (!File.Exists(project.FullPath)) { AssetStatus = "プロジェクトが見つかりません。移動先の関連付けは未対応です。"; return; }
            var projectPath = project.FullPath;
            var expected = await ProjectFileEvidence.ReadAsync(projectPath);
            if (MessageBox.Show($"次の一覧項目に新しい ID を設定しますか？\n{project.FullPath}\n既存 metadata は関連付けられません。", "プロジェクト ID", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
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
        var project = SelectedProject;
        if (project is null || project.ProjectId is not null || HasUnsavedProjectList) return;
        if (!isProjectListLossless) { AssetStatus = "一覧に安全に保存できない項目があります。関連付けを中止しました。"; return; }
        var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json", Title = "関連付ける旧素材メタデータを選択", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        await ExecuteWithBusyAsync("Associate legacy metadata", async () =>
        {
            var projectPath = project.FullPath;
            var projectEvidence = await ProjectFileEvidence.ReadAsync(projectPath);
            var before = await ReadMetadataEvidenceAsync(dialog.FileName).ConfigureAwait(true);
            var manifest = AssetManifestStore.Parse(before);
            var afterLoad = await ReadMetadataEvidenceAsync(dialog.FileName).ConfigureAwait(true);
            if (!before.SequenceEqual(afterLoad)) throw new InvalidDataException("metadata が変更されました。再選択してください。");
            if (!ReferenceEquals(project, SelectedProject) || project.ProjectId is not null || HasUnsavedProjectList || project.FullPath != projectPath) return;
            var confirm = MessageBox.Show($"一覧項目: {projectPath}\n選択 metadata: {dialog.FileName}\nmetadata の project 名: {manifest.ProjectFileName}\nproject ID: {manifest.ProjectId}\nrevision ID: {manifest.RevisionId}\n\nmetadata の場所は保存しません。今後もファイルを選択する必要があります。名前や内容だけでは同一性を確認できません。同じ論理プロジェクトとして関連付けますか？", "旧素材メタデータ", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;
            var afterConfirm = await ReadMetadataEvidenceAsync(dialog.FileName).ConfigureAwait(true);
            if (!before.SequenceEqual(afterConfirm) || project.FullPath != projectPath) throw new InvalidDataException("metadata または project が変更されました。再確認してください。");
            await AssociateLegacyMetadataAsync(project, manifest, projectEvidence).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private static async Task<byte[]> ReadMetadataEvidenceAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("素材 metadata が大きすぎます。");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes).ConfigureAwait(false);
        return bytes;
    }

    internal async Task AssociateLegacyMetadataAsync(ProjectEntry project, AssetManifest manifest, ProjectFileIdentity? expectedProject = null)
    {
        if (!isInitialized || !ReferenceEquals(project, SelectedProject) || project.ProjectId is not null || HasUnsavedProjectList)
            throw new InvalidOperationException("一覧の状態が変わりました。再確認してください。");
        if (!isProjectListLossless) throw new InvalidDataException("一覧に安全に保存できない項目があります。関連付けを中止しました。");
        if (manifest.ProjectId == Guid.Empty || !File.Exists(project.FullPath))
            throw new InvalidDataException("プロジェクトまたは metadata ID が不正です。");
        if (Projects.Any(item => !ReferenceEquals(item, project) && item.ProjectId == manifest.ProjectId))
            throw new InvalidDataException("この metadata の ID は別の一覧項目に使用されています。");
        var projectPath = project.FullPath;
        await using var file = ProjectFileEvidence.OpenRead(projectPath);
        var projectEvidence = await ProjectFileEvidence.ReadAsync(file);
        if (expectedProject is not null && projectEvidence != expectedProject) throw new InvalidDataException("project が変更されました。再確認してください。");
        if (!ReferenceEquals(project, SelectedProject) || project.FullPath != projectPath || project.ProjectId is not null || HasUnsavedProjectList)
            throw new InvalidDataException("project または一覧が変更されました。再確認してください。");
        SetPendingIdentity(project, manifest.ProjectId, projectEvidence);
        await SaveAsync().ConfigureAwait(true);
        AssetStatus = HasUnsavedProjectList ? "ID を保存しましたが、保存中の一覧変更が未保存です。" : "既存の project ID を一覧に関連付けました。metadata と project ファイルは変更していません。";
    }

    private sealed record PendingIdentity(string Path, Guid Id, string Fingerprint, ProjectFileIdentity Evidence);

    private string CurrentStoreFingerprint() => ProjectListRecovery.Fingerprint(new ProjectStore { Projects = Projects.ToList(), Folders = folders.ToList() });

    private void SetPendingIdentity(ProjectEntry project, Guid identity, ProjectFileIdentity evidence)
    {
        if (Projects.Any(item => !ReferenceEquals(item, project) && item.FileIdentity?.IsSameFile(evidence) == true))
            throw new InvalidDataException("同じファイルが別のパスから登録されています。");
        var before = CurrentStoreFingerprint();
        var canRecover = !HasUnsavedProjectList && lastSavedFingerprint is not null && before == lastSavedFingerprint;
        project.ProjectId = identity;
        project.FileIdentity = evidence;
        var current = new ProjectStore { Projects = Projects.ToList(), Folders = folders.ToList() };
        pendingIdentity = canRecover && ProjectListRecovery.Fingerprint(current, project.FullPath) == before
            ? new PendingIdentity(project.FullPath, identity, ProjectListRecovery.Fingerprint(current), evidence)
            : null;
    }

    private async Task RecoverIdentityAsync()
    {
        var pending = pendingIdentity;
        if (pending is null || !HasUnsavedProjectList) return;
        await ExecuteWithBusyAsync("Recover project identity", async () =>
        {
            if (CurrentStoreFingerprint() != pending.Fingerprint) throw new InvalidDataException("未保存の一覧に他の変更があるため自動再適用できません。");
            var latest = await repository.LoadAsync().ConfigureAwait(true);
            await using var file = ProjectFileEvidence.OpenRead(pending.Path);
            if (CurrentStoreFingerprint() != pending.Fingerprint || await ProjectFileEvidence.ReadAsync(file) != pending.Evidence)
                throw new InvalidDataException("未保存の一覧または project が変更されました。");
            ProjectListRecovery.ApplyIdentity(latest, pending.Path, pending.Id, pending.Evidence);
            var previousPaths = Projects.Select(x => x.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var latestPaths = latest.Projects.Select(x => x.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = latestPaths.Except(previousPaths, StringComparer.OrdinalIgnoreCase).Count();
            var removed = previousPaths.Except(latestPaths, StringComparer.OrdinalIgnoreCase).Count();
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

    private async Task RecoverProjectListAsync()
    {
        var baseline = savedBaseline;
        if (baseline is null || !HasUnsavedProjectList) return;
        await ExecuteWithBusyAsync("Recover project list", async () =>
        {
            var local = ProjectListMerge.Snapshot(new ProjectStore { Projects = Projects.ToList(), Folders = folders.ToList(), Revision = projectListRevision, IsRecoverySafe = isProjectListLossless });
            var fingerprint = ProjectListRecovery.Fingerprint(local);
            var latest = await repository.LoadAsync().ConfigureAwait(true);
            if (CurrentStoreFingerprint() != fingerprint) throw new InvalidDataException("未保存の一覧が変更されました。再確認してください。");
            var plan = MergeWithReview(baseline, local, latest);
            if (plan is null) return;
            if (plan.Conflicts.Count != 0) throw new InvalidDataException(string.Join("\n", plan.Conflicts.Take(20)) + (plan.Conflicts.Count > 20 ? "\n他にも競合があります。" : ""));
            if (plan.Changes.Count == 0) throw new InvalidDataException("再適用する変更がありません。ID 設定は専用の回復操作を使用してください。");
            if (plan.Changes.Count > 20) throw new InvalidDataException("変更が20件を超えています。すべてを表示できないため再適用を中止しました。");
            var merged = plan.Store!;
            using var verifiedFiles = await ProjectListRecoveryFiles.OpenAsync(merged, latest,
                path => pendingAdditionEvidence.TryGetValue(path, out var evidence) ? evidence : null);
            var summary = string.Join("\n", plan.Changes);
            if (summary.Length > 12000) throw new InvalidDataException("変更内容が大きすぎて確認できないため再適用を中止しました。");
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

    private async Task ExportRecoverySnapshotAsync()
    {
        var snapshot = retainedRecoverySnapshot;
        if (snapshot is null) return;
        await ExportSnapshotAsync(snapshot).ConfigureAwait(true);
    }

    private async Task ExportRecoveryScreenAsync()
    {
        var snapshot = retainedScreenSnapshot;
        if (snapshot is null) return;
        await ExportSnapshotAsync(snapshot).ConfigureAwait(true);
    }

    private async Task ExportSnapshotAsync(ProjectStore snapshot)
    {
        var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", Title = "未保存一覧を新しいファイルに退避", OverwritePrompt = false };
        if (dialog.ShowDialog() != true) return;
        await ExecuteWithBusyAsync("Export recovery snapshot", async () =>
        {
            var exporter = new JsonProjectRepository(logger, dialog.FileName);
            var copy = ProjectListMerge.Snapshot(snapshot);
            await exporter.SaveNewCopyAsync(copy).ConfigureAwait(true);
            AssetStatus = "未保存一覧を新しいファイルへ退避しました。元の画面と一覧は変更していません。";
        }).ConfigureAwait(true);
    }

    private async Task SaveAsync()
    {
        if (!isInitialized)
        {
            throw new InvalidOperationException("プロジェクト一覧を読み込めなかったため保存できません。");
        }

        try
        {
            if (!isProjectListLossless) throw new InvalidDataException("一覧に安全に保存できない項目があります。元ファイルを保護するため保存を中止しました。");
            var store = ProjectListMerge.Snapshot(new ProjectStore
            {
                Projects = Projects.ToList(),
                Folders = folders.ToList(),
                Revision = projectListRevision,
                IsRecoverySafe = isProjectListLossless,
            });
            var expectedFingerprint = ProjectListRecovery.Fingerprint(store);
            await SavePendingDraftAsync(store);
            await SaveRepositoryWithPendingDraftAsync(store);
            projectListRevision = store.Revision;
            isProjectListLossless = store.IsRecoverySafe;
            lastSavedFingerprint = CurrentStoreFingerprint() == expectedFingerprint ? expectedFingerprint : null;
            savedBaseline = store;
            pendingIdentity = null;
            pendingAdditionEvidence.Clear();
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
        catch (InvalidDataException ex) when (operationName == "GenerationSave")
        {
            logger.Error(ex, "世代 manifest の読み込みに失敗しました。");
            logger.Flush();
            MessageBox.Show("世代情報を読み込めません。既存データを保護するため保存を中止しました。ログを確認してください。", "世代管理", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private static FileLogger CreateLogger()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(ProjectListViewModel).Assembly.Location) ?? AppContext.BaseDirectory;
        var logPath = Path.Combine(assemblyDir, "logs", "YMMProjectManager.log");
        return new FileLogger(logPath);
    }

    private void PrepareThumbnailMetadata(IReadOnlyList<ProjectEntry> entries)
    {
        foreach (var entry in entries)
        {
            UpdateThumbnailMetadata(entry);
        }
    }

    private void UpdateThumbnailMetadata(ProjectEntry entry)
    {
        try
        {
            // キャッシュキーはプロジェクトパス由来なので、移動や別名保存時も再計算する。
            var hash = FilmstripCacheKeyFactory.TryCreateHash(entry.FullPath);
            if (string.IsNullOrWhiteSpace(hash))
            {
                entry.ThumbnailCacheDirectory = null;
                entry.ThumbnailSource = null;
                return;
            }

            var cacheDirectory = Path.Combine(
                AppDirectories.UserDirectory,
                "plugin",
                "YMMProjectManager",
                "cache",
                "filmstrip",
                hash);
            entry.ThumbnailCacheDirectory = null;
            entry.ThumbnailCacheDirectory = cacheDirectory;

            var thumbPath = Path.Combine(cacheDirectory, "000.png");
            if (!File.Exists(thumbPath))
            {
                entry.ThumbnailSource = null;
            }
        }
        catch (Exception ex)
        {
            entry.ThumbnailCacheDirectory = null;
            entry.ThumbnailSource = null;
            logger.Error($"UpdateThumbnailMetadata failed: {entry.FullPath}", ex);
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

    private static Window? GetActiveWindow()
    {
        return System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive);
    }

    private static List<int> CreateSeekProbeFrames(object timeline)
    {
        var frames = new List<int> { 0, 10, 100, 500, 1000 };
        var lastNearFrame = TryGetTimelineLastNearFrame(timeline);
        if (lastNearFrame is int candidate && candidate >= 0 && !frames.Contains(candidate))
        {
            frames.Add(candidate);
        }

        return frames;
    }

    private static int? TryGetTimelineLastNearFrame(object timeline)
    {
        var lengthProperty = timeline.GetType().GetProperty("Length");
        var lengthValue = lengthProperty?.GetValue(timeline);
        if (!TryConvertFrameLikeValue(lengthValue, out var lengthFrames))
        {
            return null;
        }

        return Math.Max(0, lengthFrames - 1);
    }

    private static bool TryConvertFrameLikeValue(object? value, out int frame)
    {
        frame = 0;
        if (value is int intValue)
        {
            frame = intValue;
            return true;
        }

        if (value is long longValue)
        {
            frame = longValue > int.MaxValue ? int.MaxValue : (int)longValue;
            return true;
        }

        var frameProperty = value?.GetType().GetProperty("Frame");
        var frameValue = frameProperty?.GetValue(value);
        if (frameValue is int nestedInt)
        {
            frame = nestedInt;
            return true;
        }

        if (frameValue is long nestedLong)
        {
            frame = nestedLong > int.MaxValue ? int.MaxValue : (int)nestedLong;
            return true;
        }

        return false;
    }
}
