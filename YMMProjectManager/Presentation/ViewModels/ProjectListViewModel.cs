using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.VisualBasic;
using Microsoft.Win32;
using YMMProjectManager.Application;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Output;
using YMMProjectManager.Infrastructure.Packaging;
using YMMProjectManager.Presentation.Commands;
using YMMProjectManager.Presentation.Relink;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed class ProjectListViewModel : ViewModelBase, ITimelineToolViewModel
{
    private readonly FileLogger logger; private readonly IProjectRepository repository; private readonly FastClipboardThumbnailGenerator fastThumbnailGenerator; private readonly YmmpBundleService bundleService;
    private ProjectEntry? selectedProject; private LinkedYmmpFile? selectedLinkedYmmp; private ProjectFolder? selectedFolderFilter; private bool isBusy; private bool isInitialized; private string frameIndexText = "0"; private string bundleStatus = string.Empty; private double bundleProgress;
    public ObservableCollection<ProjectEntry> Projects { get; } = []; public ObservableCollection<ProjectFolder> Folders { get; } = []; public ICollectionView FilteredProjects { get; }
    public static IReadOnlyList<YmmpRole> RoleOptions { get; } = Enum.GetValues<YmmpRole>();
    public bool IsBusy { get => isBusy; private set { if (SetProperty(ref isBusy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public ProjectEntry? SelectedProject { get=>selectedProject; set { if (SetProperty(ref selectedProject, value)){ SelectedLinkedYmmp = selectedProject?.LinkedYmmpFiles.FirstOrDefault(); CommandManager.InvalidateRequerySuggested(); } } }
    public LinkedYmmpFile? SelectedLinkedYmmp { get=>selectedLinkedYmmp; set => SetProperty(ref selectedLinkedYmmp, value); }
    public ProjectFolder? SelectedFolderFilter { get=>selectedFolderFilter; set { if (SetProperty(ref selectedFolderFilter, value)) FilteredProjects.Refresh(); } }
    public string FrameIndexText { get => frameIndexText; set => SetProperty(ref frameIndexText, value); }
    public string BundleStatus { get => bundleStatus; set => SetProperty(ref bundleStatus, value); }
    public double BundleProgress { get => bundleProgress; set => SetProperty(ref bundleProgress, value); }

    public ICommand AddCommand { get; } public ICommand RemoveCommand { get; } public ICommand OpenCommand { get; } public ICommand GenerateThumbnailsFastCommand { get; } public ICommand ShowTimelineContextStatusCommand { get; } public ICommand GoToFrameCommand { get; } public ICommand CopyPreviewCommand { get; } public ICommand OpenRelinkWindowCommand { get; } public ICommand PackageSelectedProjectCommand { get; } public ICommand PackageOpenedProjectCommand { get; } public ICommand ExtractBundleCommand { get; }
    public ICommand AddFolderCommand { get; } public ICommand RenameFolderCommand { get; } public ICommand DeleteFolderCommand { get; } public ICommand SetUncategorizedFilterCommand { get; } public ICommand AddLinkedYmmpCommand { get; } public ICommand RemoveLinkedYmmpCommand { get; } public ICommand SaveLinkedYmmpCommand { get; } public ICommand AssignSelectedProjectToFolderCommand { get; }

    public ProjectListViewModel() : this(CreateLogger(), null) {}
    internal ProjectListViewModel(FileLogger logger, IProjectRepository? repository)
    {
        this.logger = logger; this.repository = repository ?? new JsonProjectRepository(logger); fastThumbnailGenerator = new FastClipboardThumbnailGenerator(logger); bundleService = new YmmpBundleService(logger);
        FilteredProjects = CollectionViewSource.GetDefaultView(Projects); FilteredProjects.Filter = x => FilterProject(x as ProjectEntry);
        AddCommand = new AsyncRelayCommand(() => AddProjectsAsync(), () => !IsBusy); RemoveCommand = new AsyncRelayCommand(RemoveAsync, () => !IsBusy && SelectedProject is not null); OpenCommand = new AsyncRelayCommand(OpenAsync, () => !IsBusy && SelectedProject is not null);
        GenerateThumbnailsFastCommand = new AsyncRelayCommand(GenerateThumbnailsFastAsync, () => !IsBusy && SelectedProject is not null); ShowTimelineContextStatusCommand = new AsyncRelayCommand(ShowTimelineContextStatusAsync, () => !IsBusy); GoToFrameCommand = new AsyncRelayCommand(GoToFrameAsync, () => !IsBusy); CopyPreviewCommand = new AsyncRelayCommand(CopyPreviewAsync, () => !IsBusy);
        OpenRelinkWindowCommand = new AsyncRelayCommand(OpenOpenedProjectRelinkWindowAsync, () => !IsBusy && TimelineContextService.Timeline is not null); PackageSelectedProjectCommand = new AsyncRelayCommand(PackageSelectedProjectAsync, () => !IsBusy && SelectedProject is not null); PackageOpenedProjectCommand = new AsyncRelayCommand(PackageOpenedProjectAsync, () => !IsBusy && TimelineContextService.Timeline is not null); ExtractBundleCommand = new AsyncRelayCommand(ExtractBundleAsync, () => !IsBusy);
        AddFolderCommand = new AsyncRelayCommand(AddFolderAsync, () => !IsBusy); RenameFolderCommand = new AsyncRelayCommand(RenameFolderAsync, () => !IsBusy && SelectedFolderFilter is not null); DeleteFolderCommand = new AsyncRelayCommand(DeleteFolderAsync, () => !IsBusy && SelectedFolderFilter is not null);
        SetUncategorizedFilterCommand = new RelayCommand(() => SelectedFolderFilter = null, () => !IsBusy);
        AddLinkedYmmpCommand = new AsyncRelayCommand(AddLinkedYmmpAsync, () => !IsBusy && SelectedProject is not null); RemoveLinkedYmmpCommand = new AsyncRelayCommand(RemoveLinkedYmmpAsync, () => !IsBusy && SelectedProject is not null && SelectedLinkedYmmp is not null); SaveLinkedYmmpCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        AssignSelectedProjectToFolderCommand = new AsyncRelayCommand(AssignSelectedProjectToFolderAsync, () => !IsBusy && SelectedProject is not null);
    }
    private bool FilterProject(ProjectEntry? p){ if (p is null) return false; if (SelectedFolderFilter is null) return p.FolderId is null; return p.FolderId == SelectedFolderFilter.Id; }
    public void SetTimelineToolInfo(TimelineToolInfo info){ TimelineContextService.Info = info; CommandManager.InvalidateRequerySuggested(); }
    public async Task InitializeAsync(){ if (isInitialized) return; await ExecuteWithBusyAsync("Load", async ()=> { var store = await repository.LoadAsync().ConfigureAwait(true); Projects.Clear(); Folders.Clear(); foreach(var f in store.Folders.OrderBy(x=>x.DisplayOrder).ThenBy(x=>x.Name)) Folders.Add(f); foreach(var p in store.Projects) Projects.Add(p); isInitialized = true; PrepareThumbnailMetadata(Projects.ToList()); RefreshLinkedYmmpExists(); FilteredProjects.Refresh(); }).ConfigureAwait(true); }
    public async Task AddProjectsAsync(IEnumerable<string>? explicitPaths = null){ await ExecuteWithBusyAsync("Add batch", async ()=> { var paths = explicitPaths?.ToArray(); if(paths is null){ var dialog = new OpenFileDialog{ Filter="YMM Project (*.ymmp)|*.ymmp", CheckFileExists=true, Multiselect=true, Title="Select .ymmp projects"}; if(dialog.ShowDialog()!=true) return; paths = dialog.FileNames; } var existing = new HashSet<string>(Projects.Select(x=>x.FullPath), StringComparer.OrdinalIgnoreCase); var added=new List<ProjectEntry>(); foreach(var raw in paths){ if(!raw.EndsWith(".ymmp", StringComparison.OrdinalIgnoreCase)) continue; string fullPath; try { fullPath = Path.GetFullPath(raw);} catch {continue;} if(!File.Exists(fullPath)) continue; if(!existing.Add(fullPath)) continue; var entry=new ProjectEntry{ FullPath=fullPath, DisplayName=Path.GetFileNameWithoutExtension(fullPath)}; entry.LinkedYmmpFiles.Add(new LinkedYmmpFile{ FilePath=fullPath, DisplayName=entry.DisplayName, Role=YmmpRole.Main, Exists=true, LastCheckedAt=DateTimeOffset.Now}); Projects.Add(entry); SelectedProject=entry; added.Add(entry);} if(added.Count>0){ await SaveAsync().ConfigureAwait(true); PrepareThumbnailMetadata(added);} FilteredProjects.Refresh(); }).ConfigureAwait(true); }
    private async Task AddFolderAsync(){ var name = Interaction.InputBox("フォルダー名を入力してください", "フォルダー作成", ""); if(string.IsNullOrWhiteSpace(name)) return; Folders.Add(new ProjectFolder{ Name=name.Trim(), DisplayOrder=Folders.Count}); await SaveAsync(); }
    private async Task RenameFolderAsync(){ if(SelectedFolderFilter is null) return; var name = Interaction.InputBox("新しいフォルダー名を入力してください", "フォルダー名変更", SelectedFolderFilter.Name); if(string.IsNullOrWhiteSpace(name)) return; SelectedFolderFilter.Name=name.Trim(); await SaveAsync(); }
    private async Task DeleteFolderAsync(){ if(SelectedFolderFilter is null) return; var id=SelectedFolderFilter.Id; Folders.Remove(SelectedFolderFilter); foreach(var p in Projects.Where(x=>x.FolderId==id)) p.FolderId=null; SelectedFolderFilter=null; await SaveAsync(); FilteredProjects.Refresh(); }
    private async Task AssignSelectedProjectToFolderAsync(){ if(SelectedProject is null) return; SelectedProject.FolderId = SelectedFolderFilter?.Id; await SaveAsync(); FilteredProjects.Refresh(); }
    private async Task AddLinkedYmmpAsync(){ if(SelectedProject is null) return; var dialog = new OpenFileDialog{ Filter="YMM Project (*.ymmp)|*.ymmp", CheckFileExists=true, Multiselect=true, Title="関連 .ymmp を選択"}; if(dialog.ShowDialog()!=true) return; foreach(var file in dialog.FileNames){ var full=Path.GetFullPath(file); if(SelectedProject.LinkedYmmpFiles.Any(x=>string.Equals(x.FilePath,full,StringComparison.OrdinalIgnoreCase))) continue; SelectedProject.LinkedYmmpFiles.Add(new LinkedYmmpFile{ FilePath=full, DisplayName=Path.GetFileNameWithoutExtension(full), Role=YmmpRole.Unknown, Exists=File.Exists(full), LastCheckedAt=DateTimeOffset.Now}); } await SaveAsync(); }
    private async Task RemoveLinkedYmmpAsync(){ if(SelectedProject is null || SelectedLinkedYmmp is null) return; SelectedProject.LinkedYmmpFiles.Remove(SelectedLinkedYmmp); SelectedLinkedYmmp = SelectedProject.LinkedYmmpFiles.FirstOrDefault(); await SaveAsync(); }
    private async Task RemoveAsync(){ if (SelectedProject is null) return; await ExecuteWithBusyAsync("Remove", async ()=> { var target = SelectedProject; if(target is null) return; Projects.Remove(target); SelectedProject = null; await SaveAsync().ConfigureAwait(true); FilteredProjects.Refresh(); }).ConfigureAwait(true); }
    private async Task OpenAsync(){ if (SelectedProject is null) return; await ExecuteWithBusyAsync("Open", async ()=> { var project = SelectedProject; if (project is null) return; if (!TryGetOpenableProjectPath(project.FullPath, out var pathToOpen, out var reason)){ MessageBox.Show(reason); return; } Process.Start(new ProcessStartInfo { FileName = pathToOpen, UseShellExecute = true, }); project.LastAccess = DateTimeOffset.Now; await SaveAsync().ConfigureAwait(true); }).ConfigureAwait(true); }
    private async Task OpenOpenedProjectRelinkWindowAsync(){ await ExecuteWithBusyAsync("OpenOpenedProjectRelinkWindow", ()=> { var info = TimelineContextService.Info; if (info?.Timeline is null){ MessageBox.Show("開いているPFが見つかりませんでした。"); return Task.CompletedTask; } OpenRelinkWindowDialog(new RelinkMainWindow(info, logger, YmmProjectPathResolver.TryGetCurrentProjectPath() ?? SelectedProject?.FullPath)); return Task.CompletedTask; }).ConfigureAwait(true); }
    private async Task PackageSelectedProjectAsync(){ var path = SelectedProject?.FullPath; if (!string.IsNullOrWhiteSpace(path)) await PackageProjectAsync(path).ConfigureAwait(true); }
    private async Task PackageOpenedProjectAsync(){ var currentProjectPath = YmmProjectPathResolver.TryGetCurrentProjectPath(); if (string.IsNullOrWhiteSpace(currentProjectPath)){ MessageBox.Show("開いているPFが見つかりませんでした。"); return; } await PackageProjectAsync(currentProjectPath).ConfigureAwait(true); }
    private async Task PackageProjectAsync(string sourcePath)
    {
        if (!TryGetOpenableProjectPath(sourcePath, out var ymmpPath, out var reason))
        {
            MessageBox.Show(reason, "同梱展開", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var defaultOutput = Path.ChangeExtension(ymmpPath, ".ymmpx");
        var dialog = new SaveFileDialog
        {
            Filter = "YMM同梱ファイル (*.ymmpx)|*.ymmpx",
            Title = "同梱ファイルの保存先を選択",
            FileName = Path.GetFileName(defaultOutput),
            InitialDirectory = Path.GetDirectoryName(defaultOutput),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ExecuteWithBusyAsync("PackageProject", async () =>
        {
            BundleStatus = "同梱ファイルを作成中...";
            BundleProgress = 0;

            var progress = new Progress<double>(value => BundleProgress = value * 100d);
            var (success, errorMessage, outputPath) = await bundleService
                .CreateBundleAsync(ymmpPath, dialog.FileName, CancellationToken.None, progress)
                .ConfigureAwait(true);
            if (!success)
            {
                BundleStatus = errorMessage ?? "同梱ファイル作成に失敗しました。";
                MessageBox.Show(BundleStatus, "同梱展開", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BundleProgress = 100;
            BundleStatus = $"作成完了: {outputPath}";
            MessageBox.Show(BundleStatus, "同梱展開", MessageBoxButton.OK, MessageBoxImage.Information);
        }).ConfigureAwait(true);
    }

    private async Task ExtractBundleAsync()
    {
        var bundleDialog = new OpenFileDialog
        {
            Filter = "YMM同梱ファイル (*.ymmpx)|*.ymmpx",
            Title = "展開する同梱ファイルを選択",
            CheckFileExists = true,
        };
        if (bundleDialog.ShowDialog() != true)
        {
            return;
        }

        var outputDialog = new OpenFolderDialog
        {
            Title = "展開先フォルダを選択",
        };
        if (outputDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(outputDialog.FolderName))
        {
            return;
        }

        await ExecuteWithBusyAsync("ExtractBundle", async () =>
        {
            BundleStatus = "同梱ファイルを展開中...";
            BundleProgress = 0;

            var progress = new Progress<double>(value => BundleProgress = value * 100d);
            var (success, errorMessage, restoredYmmpPath) = await bundleService
                .ExtractBundleAsync(bundleDialog.FileName, outputDialog.FolderName, CancellationToken.None, progress)
                .ConfigureAwait(true);
            if (!success)
            {
                BundleStatus = errorMessage ?? "同梱ファイル展開に失敗しました。";
                MessageBox.Show(BundleStatus, "同梱展開", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BundleProgress = 100;
            BundleStatus = $"展開完了: {restoredYmmpPath}";
            if (!string.IsNullOrWhiteSpace(restoredYmmpPath) && File.Exists(restoredYmmpPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = restoredYmmpPath,
                        UseShellExecute = true,
                    });
                    BundleStatus = $"展開して起動しました: {restoredYmmpPath}";
                }
                catch (Exception ex)
                {
                    logger.Error(ex, $"ExtractBundle auto-open failed. path={restoredYmmpPath}");
                }
            }

            MessageBox.Show(BundleStatus, "同梱展開", MessageBoxButton.OK, MessageBoxImage.Information);
        }).ConfigureAwait(true);
    }
    private void OpenRelinkWindowDialog(RelinkMainWindow window){ var owner = System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive); if (owner is not null && !ReferenceEquals(owner, window)) window.Owner = owner; window.ShowDialog(); }
    private async Task GenerateThumbnailsFastAsync(){ if (SelectedProject is null) return; await ExecuteWithBusyAsync("Generate thumbnails (fast)", async ()=> { var timeline = TimelineContextService.Timeline; if (timeline is null){ MessageBox.Show("Open a project in YMM first."); return; } var result = await fastThumbnailGenerator.GenerateAsync(SelectedProject.FullPath, timeline, CancellationToken.None).ConfigureAwait(true); if (result.Success) UpdateThumbnailMetadata(SelectedProject); }).ConfigureAwait(true); }
    private Task ShowTimelineContextStatusAsync(){ return Task.CompletedTask; }
    private async Task GoToFrameAsync(){ await ExecuteWithBusyAsync("GoToFrame", async ()=> { var timeline = TimelineContextService.Timeline; if (timeline is null) return; if (!int.TryParse(FrameIndexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frameIndex) || frameIndex < 0) return; await fastThumbnailGenerator.GoToFrameAsync(timeline, frameIndex, CancellationToken.None).ConfigureAwait(true); }).ConfigureAwait(true); }
    private async Task CopyPreviewAsync(){ await ExecuteWithBusyAsync("CopyPreview", async ()=> { if (TimelineContextService.Timeline is null) return; await fastThumbnailGenerator.CopyPreviewAsync(CancellationToken.None).ConfigureAwait(true); }).ConfigureAwait(true); }
    private async Task SaveAsync(){ await repository.SaveAsync(new ProjectStore{ Projects = Projects.ToList(), Folders = Folders.ToList()}).ConfigureAwait(true); }
    private async Task ExecuteWithBusyAsync(string operationName, Func<Task> action){ if (IsBusy) return; IsBusy = true; try { await action().ConfigureAwait(true);} finally { IsBusy = false; } }
    private static FileLogger CreateLogger(){ var assemblyDir = Path.GetDirectoryName(typeof(ProjectListViewModel).Assembly.Location) ?? AppContext.BaseDirectory; var logPath = Path.Combine(assemblyDir, "logs", "YMMProjectManager.log"); return new FileLogger(logPath); }
    private void PrepareThumbnailMetadata(IReadOnlyList<ProjectEntry> entries){ foreach (var entry in entries) UpdateThumbnailMetadata(entry); }
    private void UpdateThumbnailMetadata(ProjectEntry entry){ try{ var hash = FilmstripCacheKeyFactory.TryCreateHash(entry.FullPath); if (string.IsNullOrWhiteSpace(hash)){ entry.ThumbnailCacheDirectory = null; entry.ThumbnailSource = null; return;} var cacheDirectory = Path.Combine(AppDirectories.UserDirectory, "plugin", "YMMProjectManager", "cache", "filmstrip", hash); entry.ThumbnailCacheDirectory = cacheDirectory; var thumbPath = Path.Combine(cacheDirectory, "000.png"); if (!File.Exists(thumbPath)) entry.ThumbnailSource = null; } catch { entry.ThumbnailCacheDirectory = null; entry.ThumbnailSource = null; }}
    private void RefreshLinkedYmmpExists(){ foreach(var p in Projects) foreach(var l in p.LinkedYmmpFiles){ l.Exists=File.Exists(l.FilePath); l.LastCheckedAt=DateTimeOffset.Now; }}
    private static bool TryGetOpenableProjectPath(string sourcePath, out string fullPath, out string reason){ fullPath = string.Empty; reason = string.Empty; if (string.IsNullOrWhiteSpace(sourcePath)){ reason = "プロジェクトパスが空です。"; return false; } try { fullPath = Path.GetFullPath(sourcePath);} catch { reason = "プロジェクトパスが不正です。"; return false; } if (!fullPath.EndsWith(".ymmp", StringComparison.OrdinalIgnoreCase)){ reason = "拡張子が .ymmp のファイルのみ開けます。"; return false; } if (!File.Exists(fullPath)){ reason = "プロジェクトファイルが見つかりません。"; return false; } return true; }
}


