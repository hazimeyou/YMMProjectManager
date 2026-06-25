using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using YMMProjectManager.Application;
using YMMProjectManager.Application.Thumbnails;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Checkpoint;
using YMMProjectManager.Infrastructure.Generations;
using YMMProjectManager.Infrastructure.Output;
using YMMProjectManager.Infrastructure.Packaging;
using YMMProjectManager.Infrastructure.Thumbnails;
using YMMProjectManager.Presentation.Commands;
using YMMProjectManager.Presentation.Checkpoint;
using YMMProjectManager.Presentation.Generation;
using YMMProjectManager.Presentation.Relink;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed class ProjectListViewModel : ViewModelBase, ITimelineToolViewModel
{
    private readonly FileLogger logger;
    private readonly IProjectRepository repository;
    private readonly SeekPreviewThumbnailGenerator seekPreviewThumbnailGenerator;
    private readonly IProjectGenerationService generationService;
    private readonly ICheckpointService checkpointService;
    private readonly YmmpxLibBundleService bundleService;
    private readonly YmmpxLibInstallGuide ymmpxLibInstallGuide;
    private List<ProjectFolder> folders = [];
    private ProjectEntry? selectedProject;
    private bool isBusy;
    private bool isInitialized;
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
    public ICommand RunSeekProbeCommand { get; }
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
        // この ViewModel には、安定しているサムネイル経路だけを持たせる。
        this.logger = logger;
        this.repository = repository ?? new JsonProjectRepository(logger);
        var currentPreviewCaptureService = new CurrentPreviewCaptureService(logger);
        seekPreviewThumbnailGenerator = new SeekPreviewThumbnailGenerator(logger, currentPreviewCaptureService);
        generationService = new ProjectGenerationService(logger);
        checkpointService = new CheckpointService(logger);
        bundleService = new YmmpxLibBundleService(logger);
        ymmpxLibInstallGuide = new YmmpxLibInstallGuide(logger);

        AddCommand = new AsyncRelayCommand(() => AddProjectsAsync(), () => !IsBusy);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, () => !IsBusy && SelectedProject is not null);
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => !IsBusy && SelectedProject is not null);
        GenerateThumbnailsFastCommand = new AsyncRelayCommand(GenerateThumbnailsFastAsync, () => !IsBusy && SelectedProject is not null);
        RunSeekProbeCommand = new AsyncRelayCommand(RunSeekProbeAsync, () => !IsBusy);
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
            var store = await repository.LoadAsync().ConfigureAwait(true);

            Projects.Clear();
            foreach (var item in store.Projects)
            {
                Projects.Add(item);
            }

            folders = store.Folders.ToList();

            isInitialized = true;
            logger.Info($"Load end. count={Projects.Count}");
            PrepareThumbnailMetadata(Projects.ToList());
            UpdateYmmpxAvailabilityStatus();
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

    public async Task CreateCheckpointAsync()
    {
        var project = SelectedProject;
        if (project is null)
        {
            MessageBox.Show("チェックポイントを作成するプロジェクトを選択してください。", "チェックポイント", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryGetOpenableProjectPath(project.FullPath, out var projectPath, out var reason))
        {
            MessageBox.Show(reason, "チェックポイント", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var currentProjectPath = YmmProjectPathResolver.TryGetCurrentProjectPath();
        if (string.IsNullOrWhiteSpace(currentProjectPath) ||
            !string.Equals(Path.GetFullPath(currentProjectPath), projectPath, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("選択中プロジェクトと、YMMで現在開いているプロジェクトが一致していません。\n一致しない場合は「現在開いているPFからチェックポイント作成」を使ってください。", "チェックポイント", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new CheckpointCreateWindow
        {
            Owner = GetActiveWindow(),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ExecuteWithBusyAsync("CheckpointCreate", async () =>
        {
            var record = await checkpointService.CreateAsync(new CheckpointCreateRequest
            {
                ProjectPath = projectPath,
                Name = dialog.CheckpointName,
                Description = dialog.Description,
                Comment = dialog.Comment,
                ThumbnailSettings = dialog.BuildSettings(),
                TimelineInfo = TimelineContextService.Info,
            }).ConfigureAwait(true);

            MessageBox.Show($"チェックポイントを作成しました。\n{record.Name}", "チェックポイント", MessageBoxButton.OK, MessageBoxImage.Information);
        }).ConfigureAwait(true);
    }

    public async Task CreateCheckpointFromOpenedProjectAsync()
    {
        var currentProjectPath = YmmProjectPathResolver.TryGetCurrentProjectPath();
        if (string.IsNullOrWhiteSpace(currentProjectPath))
        {
            MessageBox.Show("現在 YMM で開いているプロジェクトを取得できませんでした。", "チェックポイント", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryGetOpenableProjectPath(currentProjectPath, out var projectPath, out var reason))
        {
            MessageBox.Show(reason, "チェックポイント", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new CheckpointCreateWindow
        {
            Owner = GetActiveWindow(),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ExecuteWithBusyAsync("CheckpointCreateFromOpenedProject", async () =>
        {
            var record = await checkpointService.CreateAsync(new CheckpointCreateRequest
            {
                ProjectPath = projectPath,
                Name = dialog.CheckpointName,
                Description = dialog.Description,
                Comment = dialog.Comment,
                ThumbnailSettings = dialog.BuildSettings(),
                TimelineInfo = TimelineContextService.Info,
            }).ConfigureAwait(true);

            TrySelectProject(projectPath);
            MessageBox.Show($"チェックポイントを作成しました。\n{record.Name}", "チェックポイント", MessageBoxButton.OK, MessageBoxImage.Information);
        }).ConfigureAwait(true);
    }

    public async Task ShowCheckpointListAsync()
    {
        var project = SelectedProject;
        if (project is null)
        {
            MessageBox.Show("チェックポイント一覧を表示するプロジェクトを選択してください。", "チェックポイント", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryGetOpenableProjectPath(project.FullPath, out var projectPath, out var reason))
        {
            MessageBox.Show(reason, "チェックポイント", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var window = new CheckpointListWindow(projectPath, logger)
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

    private async Task PackageProjectAsync(string sourcePath)
    {
        if (!await EnsureYmmpxLibAvailableAsync(interactive: true).ConfigureAwait(true))
        {
            return;
        }

        if (!TryGetOpenableProjectPath(sourcePath, out var ymmpPath, out var reason))
        {
            MessageBox.Show(reason, "ymmpx", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var defaultOutput = Path.ChangeExtension(ymmpPath, ".ymmpx");
        var dialog = new SaveFileDialog
        {
            Filter = "YMM同梱ファイル (*.ymmpx)|*.ymmpx",
            Title = "ymmpx の保存先を選択",
            FileName = Path.GetFileName(defaultOutput),
            InitialDirectory = Path.GetDirectoryName(defaultOutput),
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ExecuteWithBusyAsync("PackageProject", async () =>
        {
            BundleStatus = "ymmpx を作成中...";
            BundleProgress = 0;

            var progress = new Progress<double>(value => BundleProgress = value * 100d);
            var result = await bundleService
                .CreatePackageAsync(ymmpPath, dialog.FileName, CancellationToken.None, progress)
                .ConfigureAwait(true);
            if (!result.Success)
            {
                if (IsYmmpxLibMissing(result.ErrorMessage))
                {
                    await ShowYmmpxLibInstallGuideAsync().ConfigureAwait(true);
                    return;
                }

                BundleStatus = result.ErrorMessage ?? "YmmpxLib を使った同梱に失敗しました。";
                MessageBox.Show(BundleStatus, "ymmpx", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BundleProgress = 100;
            BundleStatus = $"作成完了: {result.OutputPath}\n検出: {result.DetectedMaterialCount} 件 / 同梱: {result.PackagedMaterialCount} 件 / 不足: {result.MissingMaterialCount} 件";
            MessageBox.Show(BundleStatus, "ymmpx", MessageBoxButton.OK, MessageBoxImage.Information);
        }).ConfigureAwait(true);
    }

    private async Task ExtractBundleAsync()
    {
        if (!await EnsureYmmpxLibAvailableAsync(interactive: true).ConfigureAwait(true))
        {
            return;
        }

        var bundleDialog = new OpenFileDialog
        {
            Filter = "YMM同梱ファイル (*.ymmpx)|*.ymmpx",
            Title = "展開する ymmpx を選択",
            CheckFileExists = true,
        };
        if (bundleDialog.ShowDialog() != true)
        {
            return;
        }

        var outputDialog = new OpenFolderDialog
        {
            Title = "展開先フォルダーを選択",
        };
        if (outputDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(outputDialog.FolderName))
        {
            return;
        }

        await ExecuteWithBusyAsync("ExtractBundle", async () =>
        {
            BundleStatus = "ymmpx を展開中...";
            BundleProgress = 0;

            var progress = new Progress<double>(value => BundleProgress = value * 100d);
            var result = await bundleService
                .ExtractPackageAsync(bundleDialog.FileName, outputDialog.FolderName, CancellationToken.None, progress)
                .ConfigureAwait(true);
            if (!result.Success)
            {
                if (IsYmmpxLibMissing(result.ErrorMessage))
                {
                    await ShowYmmpxLibInstallGuideAsync().ConfigureAwait(true);
                    return;
                }

                BundleStatus = result.ErrorMessage ?? "YmmpxLib を使った展開に失敗しました。";
                MessageBox.Show(BundleStatus, "ymmpx", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BundleProgress = 100;
            BundleStatus = $"展開完了: {result.RestoredProjectPath}\n置換した FilePath: {result.ReplacedPathCount} 件";
            if (!string.IsNullOrWhiteSpace(result.RestoredProjectPath) && File.Exists(result.RestoredProjectPath))
            {
                await RegisterExtractedProjectAsync(result.RestoredProjectPath).ConfigureAwait(true);

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = result.RestoredProjectPath,
                        UseShellExecute = true,
                    });
                    BundleStatus = $"展開して起動しました: {result.RestoredProjectPath}";
                }
                catch (Exception ex)
                {
                    logger.Error(ex, $"展開後の自動起動に失敗しました。path={result.RestoredProjectPath}");
                }
            }

            MessageBox.Show(BundleStatus, "ymmpx", MessageBoxButton.OK, MessageBoxImage.Information);
        }).ConfigureAwait(true);
    }

    private async Task RegisterExtractedProjectAsync(string restoredYmmpPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(restoredYmmpPath);
            if (Projects.Any(x => string.Equals(Path.GetFullPath(x.FullPath), fullPath, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var entry = new ProjectEntry
            {
                FullPath = fullPath,
                DisplayName = Path.GetFileNameWithoutExtension(fullPath),
            };

            Projects.Add(entry);
            SelectedProject = entry;
            PrepareThumbnailMetadata([entry]);
            await SaveAsync().ConfigureAwait(true);
            logger.Info($"展開後のプロジェクトを一覧に登録しました。path={fullPath}");
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"展開後のプロジェクト登録に失敗しました。path={restoredYmmpPath}");
        }
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

    private void UpdateYmmpxAvailabilityStatus()
    {
        if (bundleService.TryEnsureAvailable(out _))
        {
            if (BundleStatus.Contains("YmmpxLibPlugin が未導入です。", StringComparison.Ordinal))
            {
                BundleStatus = string.Empty;
            }

            return;
        }

        logger.Info($"YmmpxLibPlugin 未導入を検出しました。探索パス={string.Join(" | ", bundleService.SearchedPaths)}");
        logger.Flush();
        BundleStatus = ymmpxLibInstallGuide.GetPassiveStatusMessage();
    }

    private async Task<bool> EnsureYmmpxLibAvailableAsync(bool interactive)
    {
        if (bundleService.TryEnsureAvailable(out _))
        {
            return true;
        }

        logger.Info($"YmmpxLibPlugin 未導入を検出しました。探索パス={string.Join(" | ", bundleService.SearchedPaths)}");
        logger.Flush();
        BundleStatus = ymmpxLibInstallGuide.GetPassiveStatusMessage();
        if (interactive)
        {
            await ShowYmmpxLibInstallGuideAsync().ConfigureAwait(true);
        }

        return false;
    }

    private async Task ShowYmmpxLibInstallGuideAsync()
    {
        var legacyPaths = ymmpxLibInstallGuide.FindLegacyFolders(AppContext.BaseDirectory);
        var message = ymmpxLibInstallGuide.BuildMissingPluginMessage(bundleService.SearchedPaths, legacyPaths);

        logger.Info("YmmpxLibPlugin の導入案内を表示しました。");
        logger.Flush();

        var answer = MessageBox.Show(
            message,
            "ymmpx",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information,
            MessageBoxResult.Yes);

        if (answer is not MessageBoxResult.Yes)
        {
            logger.Info("YmmpxLibPlugin の導入案内でユーザーがキャンセルを選びました。");
            logger.Flush();
            return;
        }

        logger.Info("YmmpxLibPlugin の導入案内でユーザーがダウンロードを選びました。");
        logger.Flush();

        var installResult = await ymmpxLibInstallGuide.DownloadAndLaunchInstallerAsync().ConfigureAwait(true);
        if (!installResult.Success)
        {
            MessageBox.Show(
                installResult.ErrorMessage ?? "YmmpxLibPlugin のダウンロードまたは起動に失敗しました。ログを確認してください。",
                "ymmpx",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show(
            "YmmpxLibPlugin のインストーラーを起動しました。\nインストール後に YMM4 を再起動してください。",
            "ymmpx",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static bool IsYmmpxLibMissing(string? errorMessage)
    {
        return !string.IsNullOrWhiteSpace(errorMessage) &&
               errorMessage.Contains("YmmpxLib が見つかりません", StringComparison.Ordinal);
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

    private async Task SaveAsync()
    {
        await repository.SaveAsync(new ProjectStore
        {
            Projects = Projects.ToList(),
            Folders = folders.ToList(),
        }).ConfigureAwait(true);
    }

    private async Task ExecuteWithBusyAsync(string operationName, Func<Task> action)
    {
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
        catch (Exception ex)
        {
            logger.Error(ex, $"{operationName}失敗。");
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

    private void TrySelectProject(string projectPath)
    {
        var fullPath = Path.GetFullPath(projectPath);
        SelectedProject = Projects.FirstOrDefault(x => string.Equals(Path.GetFullPath(x.FullPath), fullPath, StringComparison.OrdinalIgnoreCase));
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
