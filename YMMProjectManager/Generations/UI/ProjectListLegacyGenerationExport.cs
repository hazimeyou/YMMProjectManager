using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using YMMProjectManager.Infrastructure.Generations;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectListViewModel
{
    public ICommand ExportLegacyGenerationCommand { get; }

    private Task ExportLegacyGenerationAsync() => ExecuteToolAsync("Asset legacy generation export", async token =>
    {
        const string title = "旧世代からPFを取り出す";
        var projects = Path.Combine(LegacyGenerationExport.StorageRoot, "projects");
        var picker = new OpenFileDialog
        {
            Title = "旧世代の metadata.json を選択",
            Filter = "旧世代の記録 (metadata.json)|metadata.json",
            InitialDirectory = Directory.Exists(projects) ? projects : null,
            CheckFileExists = true,
        };
        if (picker.ShowDialog() != true) return;

        try
        {
            var snapshot = await LegacyGenerationExport.InspectAsync(picker.FileName, token);
            var output = new SaveFileDialog
            {
                Title = "確認した旧世代を別名PFとして保存",
                Filter = "YMM Project (*.ymmp)|*.ymmp",
                InitialDirectory = Path.GetDirectoryName(snapshot.OriginalPath),
                FileName = Path.GetFileNameWithoutExtension(snapshot.OriginalPath) + "-旧世代.ymmp",
                DefaultExt = ".ymmp",
                AddExtension = true,
                OverwritePrompt = false,
            };
            if (MessageBox.Show(
                $"世代 {snapshot.GenerationId}\n元PF {snapshot.OriginalPath}\n\n選択した旧世代のサイズと SHA-256 を照合して、新しいPFに保存します。別のフォルダーに保存すると、相対素材パスの解決先が変わる場合があります。",
                title, MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel) != MessageBoxResult.OK ||
                output.ShowDialog() != true)
                return;

            await LegacyGenerationExport.ExportAsync(snapshot, output.FileName, token);
            AssetStatus = $"旧世代のPFを別名で保存しました。\n{output.FileName}";
            MessageBox.Show(AssetStatus, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            logger.Info($"Legacy generation export stopped ({ex.GetType().Name}, 0x{ex.HResult:X8}).");
            AssetStatus = "旧世代のPFを保存できませんでした。元PFと旧世代データを保全しています。";
            MessageBox.Show($"{AssetStatus}\n{ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    });
}
