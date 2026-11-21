using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectListViewModel
{
    public ICommand ReviewProjectCleanupCommand { get; }

    private Task ReviewProjectCleanupAsync() => ExecuteToolAsync("Asset PF cleanup", async token =>
    {
        var project = SelectedProject;
        if (project is null) return;
        var path = project.FullPath;
        var service = new ProjectFileCleanupService();
        var preview = await service.InspectAsync(path, token);
        if (!ReferenceEquals(project, SelectedProject) || project.FullPath != path) return;
        var report = preview.FormatForDisplay();
        if (!preview.CanSave)
        {
            MessageBox.Show(report, "PF 整理の確認", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(report + "\n\n別名のPFに保存しますか？", "PF 整理の確認", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var output = new SaveFileDialog
        {
            Title = "元のプロジェクトと同じフォルダーに別名で保存",
            Filter = "YMM Project (*.ymmp)|*.ymmp",
            InitialDirectory = Path.GetDirectoryName(path),
            FileName = Path.GetFileNameWithoutExtension(path) + "-整理済み.ymmp",
            OverwritePrompt = false,
        };
        if (output.ShowDialog() != true) return;
        await service.SaveAsAsync(preview, output.FileName, token);
        AssetStatus = $"整理したPFを保存しました。\n{output.FileName}";
    });
}
