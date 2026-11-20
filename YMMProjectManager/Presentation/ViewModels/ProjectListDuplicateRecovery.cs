using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectListViewModel
{
    public ICommand RepairDuplicateProjectIdsCommand { get; }

    private async Task RepairDuplicateProjectIdsAsync()
    {
        var published = false;
        await ExecuteToolAsync("Asset duplicate project ID recovery", async token =>
        {
            if (repository is not JsonProjectRepository json || HasUnsavedProjectList) return;
            var original = await json.ReadOriginalAsync(token);
            var review = await Task.Run(() => DuplicateProjectIdRecovery.Inspect(original), token);
            if (review.Conflicts.Count == 0)
            {
                AssetStatus = "重複するプロジェクト ID は見つかりませんでした。";
                return;
            }

            var keepers = ChooseDuplicateIdentityKeepers(review);
            if (keepers is null) return;
            var choice = MessageBox.Show(
                "「はい」で原本を別ファイルに退避してから修復を反映します。「いいえ」で修復版を別ファイルに保存します。",
                "重複プロジェクト ID の修復", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.No);
            if (choice == MessageBoxResult.Cancel) return;
            var apply = choice == MessageBoxResult.Yes;
            var picker = new SaveFileDialog
            {
                Filter = "JSON (*.json)|*.json",
                DefaultExt = ".json",
                FileName = apply ? "projects.original.json" : "projects.recovered.json",
                Title = apply ? "一覧原本の退避先を選択" : "修復した一覧のコピーを保存",
                OverwritePrompt = false,
            };
            if (picker.ShowDialog() != true || HasUnsavedProjectList) return;
            if (apply)
            {
                try { await DuplicateProjectIdRecovery.ApplyAsync(json, review, keepers, picker.FileName, token); }
                catch (InvalidDataException ex)
                {
                    AssetStatus = ex.Message;
                    MessageBox.Show(ex.Message, "一覧の修復", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                isInitialized = false;
                published = true;
                AssetStatus = $"修復を反映しました。原本の退避先\n{picker.FileName}";
            }
            else
            {
                var current = await json.ReadOriginalAsync(token);
                var recovered = await Task.Run(() => DuplicateProjectIdRecovery.Repair(current, review, keepers), token);
                await JsonProjectRepository.WriteNewCopyAsync(picker.FileName, recovered, token);
                AssetStatus = $"修復した一覧を新しいファイルに保存しました。\n{picker.FileName}";
            }
            logger.Info($"Duplicate project identity repair finished. groups={review.Conflicts.Count} applied={apply}");
        });
        if (published) await InitializeAsync();
    }

    private static Dictionary<Guid, int>? ChooseDuplicateIdentityKeepers(DuplicateProjectIdRecovery.Review review)
    {
        var grid = new DockPanel { Margin = new Thickness(12) };
        var instructions = new TextBlock
        {
            Text = "各グループで既存の ID を維持する項目を選んでください。他の項目には新しい ID を付け、コピー内で旧 ID のメタデータ関連付けを外します。現在の一覧とメタデータファイルは保持します。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        DockPanel.SetDock(instructions, Dock.Top);
        grid.Children.Add(instructions);

        var confirm = new Button { Content = "選択を確認して続ける", IsEnabled = false, IsDefault = true, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(confirm, Dock.Bottom);
        grid.Children.Add(confirm);
        var fields = new StackPanel();
        grid.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var selections = new Dictionary<Guid, ComboBox>();
        foreach (var group in review.Conflicts)
        {
            var options = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
            fields.Children.Add(new TextBlock { Text = group.Id.ToString("D") });
            foreach (var entry in group.Entries)
                options.Items.Add(new ComboBoxItem { Content = entry.Path + (entry.HasMetadata ? "（関連付けあり）" : ""), Tag = entry.Row });
            selections.Add(group.Id, options);
            options.SelectionChanged += (_, _) => confirm.IsEnabled = selections.Values.All(box => box.SelectedItem is not null);
            fields.Children.Add(options);
        }
        var window = new Window { Title = "重複プロジェクト ID の修復", Width = 760, Height = 420, Content = grid, Owner = GetActiveWindow() };
        confirm.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true
            ? selections.ToDictionary(item => item.Key, item => (int)((ComboBoxItem)item.Value.SelectedItem).Tag)
            : null;
    }
}
