using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
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
    private readonly FileLogger logger;
    private readonly IProjectRepository repository;
    private readonly FastClipboardThumbnailGenerator fastThumbnailGenerator;
    private readonly YmmpBundleService bundleService;
    private ProjectEntry? selectedProject;
    private bool isBusy;
    private bool isInitialized;
    private string frameIndexText = "0";
    private string bundleStatus = string.Empty;
    private double bundleProgress;

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
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string FrameIndexText
    {
        get => frameIndexText;
        set => SetProperty(ref frameIndexText, value);
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

    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand GenerateThumbnailsFastCommand { get; }
    public ICommand ShowTimelineContextStatusCommand { get; }
    public ICommand GoToFrameCommand { get; }
    public ICommand CopyPreviewCommand { get; }
    public ICommand OpenRelinkWindowCommand { get; }
    public ICommand PackageSelectedProjectCommand { get; }
    public ICommand PackageOpenedProjectCommand { get; }
    public ICommand ExtractBundleCommand { get; }

    public ProjectListViewModel()
        : this(CreateLogger(), null)
    {
    }

    internal ProjectListViewModel(FileLogger logger, IProjectRepository? repository)
    {
        this.logger = logger;
        this.repository = repository ?? new JsonProjectRepository(logger);
        fastThumbnailGenerator = new FastClipboardThumbnailGenerator(logger);
        bundleService = new YmmpBundleService(logger);

        AddCommand = new AsyncRelayCommand(() => AddProjectsAsync(), () => !IsBusy);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, () => !IsBusy && SelectedProject is not null);
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => !IsBusy && SelectedProject is not null);
        GenerateThumbnailsFastCommand = new AsyncRelayCommand(GenerateThumbnailsFastAsync, () => !IsBusy && SelectedProject is not null);
        ShowTimelineContextStatusCommand = new AsyncRelayCommand(ShowTimelineContextStatusAsync, () => !IsBusy);
        GoToFrameCommand = new AsyncRelayCommand(GoToFrameAsync, () => !IsBusy);
        CopyPreviewCommand = new AsyncRelayCommand(CopyPreviewAsync, () => !IsBusy);
        OpenRelinkWindowCommand = new AsyncRelayCommand(OpenOpenedProjectRelinkWindowAsync, () => !IsBusy && TimelineContextService.Timeline is not null);
        PackageSelectedProjectCommand = new AsyncRelayCommand(PackageSelectedProjectAsync, () => !IsBusy && SelectedProject is not null);
        PackageOpenedProjectCommand = new AsyncRelayCommand(PackageOpenedProjectAsync, () => !IsBusy && TimelineContextService.Timeline is not null);
        ExtractBundleCommand = new AsyncRelayCommand(ExtractBundleAsync, () => !IsBusy);
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
            var items = await repository.LoadAsync().ConfigureAwait(true);

            Projects.Clear();
            foreach (var item in items)
            {
                Projects.Add(item);
            }

            isInitialized = true;
            logger.Info($"Load end. count={Projects.Count}");
            PrepareThumbnailMetadata(Projects.ToList());
        }).ConfigureAwait(true);
    }

    public async Task AddProjectsAsync(IEnumerable<string>? explicitPaths = null)
    {
        await ExecuteWithBusyAsync("Add batch", async () =>
        {
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
                    DisplayName = Path.GetFileNameWithoutExtension(fullPath),
                };
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
            MessageBox.Show("開いているPFが見つかりませんでした。", "同梱展開", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await PackageProjectAsync(currentProjectPath).ConfigureAwait(true);
    }

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
        if (SelectedProject is null)
        {
            return;
        }

        await ExecuteWithBusyAsync("Generate thumbnails (fast)", async () =>
        {
            var timeline = TimelineContextService.Timeline;
            if (timeline is null)
            {
                MessageBox.Show("Open a project in YMM first.", "YMM Project Manager", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = await fastThumbnailGenerator
                .GenerateAsync(SelectedProject.FullPath, timeline, CancellationToken.None)
                .ConfigureAwait(true);

            if (!result.Success)
            {
                MessageBox.Show("サムネイル生成に失敗しました。ログを確認してください。", "YMM Project Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            UpdateThumbnailMetadata(SelectedProject);
        }).ConfigureAwait(true);
    }

    private Task ShowTimelineContextStatusAsync()
    {
        var timeline = TimelineContextService.Timeline;
        MessageBox.Show(
            $"Timeline null: {timeline is null}\nCurrentFrame: {(timeline?.CurrentFrame.ToString() ?? "N/A")}",
            "YMM Project Manager",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return Task.CompletedTask;
    }

    private async Task GoToFrameAsync()
    {
        await ExecuteWithBusyAsync("GoToFrame", async () =>
        {
            var timeline = TimelineContextService.Timeline;
            if (timeline is null)
            {
                MessageBox.Show("Open a project in YMM first.", "YMM Project Manager", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!int.TryParse(FrameIndexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frameIndex) || frameIndex < 0)
            {
                MessageBox.Show("FrameIndex must be a non-negative integer.", "YMM Project Manager", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await fastThumbnailGenerator.GoToFrameAsync(timeline, frameIndex, CancellationToken.None).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task CopyPreviewAsync()
    {
        await ExecuteWithBusyAsync("CopyPreview", async () =>
        {
            var timeline = TimelineContextService.Timeline;
            if (timeline is null)
            {
                MessageBox.Show("Open a project in YMM first.", "YMM Project Manager", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = await fastThumbnailGenerator.CopyPreviewAsync(CancellationToken.None).ConfigureAwait(true);
            if (result is null)
            {
                MessageBox.Show("CopyPreview failed. clipboard empty", "YMM Project Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }).ConfigureAwait(true);
    }

    private async Task SaveAsync()
    {
        await repository.SaveAsync(Projects.ToList()).ConfigureAwait(true);
    }

    private async Task ExecuteWithBusyAsync(string operationName, Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        logger.Info($"{operationName} start.");
        logger.Flush();
        try
        {
            await action().ConfigureAwait(true);
            logger.Info($"{operationName} end.");
            logger.Flush();
        }
        catch (OperationCanceledException)
        {
            logger.Info($"{operationName} canceled.");
            logger.Flush();
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"{operationName} failed.");
            logger.Flush();
            MessageBox.Show("処理中にエラーが発生しました。ログを確認してください。", "YMM Project Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
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
}
