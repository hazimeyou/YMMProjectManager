using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectListViewModel
{
    public ICommand RepairProjectListIdentitiesCommand { get; }

    private async Task RepairProjectListIdentitiesAsync()
    {
        var published = false;
        await ExecuteToolAsync("Asset duplicate project ID recovery", async token =>
        {
            if (repository is not JsonProjectRepository json || HasUnsavedProjectList) return;
            var original = await json.ReadOriginalAsync(token);
            var review = await Task.Run(() => ProjectListIdentityRecovery.Inspect(original), token);
            if (review.Conflicts.Count == 0 && review.LegacyProjects.All(entry => entry.HasMetadata))
            {
                AssetStatus = "修復できる重複 ID または未設定の項目は見つかりませんでした。";
                return;
            }

            var choices = ChooseIdentityRepairs(review);
            if (choices is null) return;
            var (keepers, legacyRows) = choices.Value;
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
                try { await ProjectListIdentityRecovery.ApplyAsync(json, review, keepers, picker.FileName, token, legacyRows); }
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
                var recovered = await Task.Run(() => ProjectListIdentityRecovery.Repair(current, review, keepers, legacyRows), token);
                await JsonProjectRepository.WriteNewCopyAsync(picker.FileName, recovered, token);
                AssetStatus = $"修復した一覧を新しいファイルに保存しました。\n{picker.FileName}";
            }
            logger.Info($"Project list identity repair finished. groups={review.Conflicts.Count} legacy={legacyRows.Count} applied={apply}");
        });
        if (published) await InitializeAsync();
    }

    private static (Dictionary<Guid, int> Keepers, HashSet<int> LegacyRows)? ChooseIdentityRepairs(ProjectListIdentityRecovery.Review review)
    {
        var grid = new DockPanel { Margin = new Thickness(12) };
        var instructions = new TextBlock
        {
            Text = "各グループで既存の ID を維持する項目を選んでください。他の項目には新しい ID を付け、旧 ID の関連付けを外します。ID 未設定の項目を選ぶと、その項目とリンクの未設定 ID を補います。元の一覧は退避し、メタデータファイルは保持します。",
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
        var legacyOptions = new Dictionary<int, CheckBox>();
        void UpdateConfirmation() => confirm.IsEnabled = selections.Values.All(box => box.SelectedItem is not null) &&
            (selections.Count > 0 || legacyOptions.Values.Any(option => option.IsChecked == true));
        foreach (var group in review.Conflicts)
        {
            var options = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
            fields.Children.Add(new TextBlock { Text = group.Id.ToString("D") });
            foreach (var entry in group.Entries)
                options.Items.Add(new ComboBoxItem { Content = entry.Path + (entry.HasMetadata ? "（関連付けあり）" : ""), Tag = entry.Row });
            selections.Add(group.Id, options);
            options.SelectionChanged += (_, _) => UpdateConfirmation();
            fields.Children.Add(options);
        }
        var legacy = review.LegacyProjects.Where(entry => !entry.HasMetadata).ToArray();
        if (legacy.Length > 0) fields.Children.Add(new TextBlock { Text = "新しい ID を付ける項目" });
        foreach (var entry in legacy)
        {
            var option = new CheckBox { Content = entry.Path, ToolTip = entry.Path, Margin = new Thickness(0, 2, 0, 2) };
            legacyOptions.Add(entry.Row, option);
            option.Checked += (_, _) => UpdateConfirmation();
            option.Unchecked += (_, _) => UpdateConfirmation();
            fields.Children.Add(option);
        }
        var window = new Window { Title = "プロジェクト一覧の ID を修復", Width = 760, Height = 420, Content = grid, Owner = GetActiveWindow() };
        confirm.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true
            ? (selections.ToDictionary(item => item.Key, item => (int)((ComboBoxItem)item.Value.SelectedItem).Tag),
                legacyOptions.Where(item => item.Value.IsChecked == true).Select(item => item.Key).ToHashSet())
            : null;
    }
}
