using System.IO;
using System.Windows;
using YMMProjectManager.Presentation.Generation;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectListViewModel
{
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
}
