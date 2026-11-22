using System.IO;
using System.Windows;
using YMMProjectManager.Presentation.Generation;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Presentation.ViewModels;

//CSJ,class=ProjectListViewModel,プロジェクト一覧の表示モデル
public sealed partial class ProjectListViewModel
{
    //CSJ,method=SaveGenerationAsync,世代を保存する（非同期）
    public async Task SaveGenerationAsync()
    {
        //CSJ,variable=project,プロジェクト
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

        //CSJ,variable=dialog,ダイアログ
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
            //CSJ,variable=displayName,表示名
            var displayName = string.IsNullOrWhiteSpace(dialog.DisplayName)
                ? DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm")
                : dialog.DisplayName.Trim();
            //CSJ,variable=memo,メモ
            var memo = string.IsNullOrWhiteSpace(dialog.Memo) ? null : dialog.Memo.Trim();

            //CSJ,variable=generation,世代
            var generation = await generationService.CreateGenerationAsync(projectPath, displayName, memo).ConfigureAwait(true);
            logger.Info($"Generation saved. projectPath={projectPath}, generationId={generation.GenerationId}");
            MessageBox.Show($"世代を保存しました。\n{generation.DisplayName}", "世代管理", MessageBoxButton.OK, MessageBoxImage.Information);
        }).ConfigureAwait(true);
    }

    //CSJ,method=ShowGenerationListAsync,世代一覧を表示する（非同期）
    public async Task ShowGenerationListAsync()
    {
        //CSJ,variable=project,プロジェクト
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

        //CSJ,variable=window,ウィンドウ
        var window = new ProjectGenerationListWindow(projectPath, logger)
        {
            Owner = GetActiveWindow(),
        };
        window.ShowDialog();
        await Task.CompletedTask;
    }

    //CSJ,method=ShowGenerationDiagnosticsAsync,世代診断情報を表示する（非同期）
    public async Task ShowGenerationDiagnosticsAsync()
    {
        //CSJ,variable=project,プロジェクト
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

        //CSJ,variable=window,ウィンドウ
        var window = new ProjectGenerationDiagnosticsWindow(projectPath, logger)
        {
            Owner = GetActiveWindow(),
        };
        window.ShowDialog();
        await Task.CompletedTask;
    }
}
