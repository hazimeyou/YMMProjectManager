using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Presentation.ViewModels;

//CSJ,class=ProjectListViewModel,プロジェクト一覧の表示モデル
public sealed partial class ProjectListViewModel
{
    //CSJ,property=RepairProjectListIdentitiesCommand,プロジェクト一覧の識別情報修復コマンド
    public ICommand RepairProjectListIdentitiesCommand { get; }

    private async Task RepairProjectListIdentitiesAsync()
    {
        //CSJ,variable=published,確定済み
        var published = false;
        await ExecuteToolAsync("Asset duplicate project ID recovery", async token =>
        {
            if (repository is not JsonProjectRepository json || HasUnsavedProjectList) return;
            var original = await json.ReadOriginalAsync(token);
            //CSJ,variable=physical,物理
            var physical = await ProjectListIdentityRecovery.InspectPhysicalAsync(original, token);
            if (physical.Conflicts.Count != 0)
            {
                published = await RepairPhysicalRegistrationsAsync(json, physical, token);
                if (published) isInitialized = false;
                return;
            }

            //CSJ,variable=review,レビュー
            var review = await Task.Run(() => ProjectListIdentityRecovery.Inspect(original), token);
            if (review.Conflicts.Count == 0 && review.LegacyProjects.All(entry => entry.HasMetadata))
            {
                AssetStatus = physical.UnverifiedRecordedDuplicates.Count == 0
                    ? "修復できる重複 ID または未設定の項目は見つかりませんでした。"
                    : "同じ物理ファイルを示す登録情報がありますが、実ファイルを確認できませんでした。\n" +
                        string.Join("\n", physical.UnverifiedRecordedDuplicates.SelectMany(group => group.Entries).Select(entry => entry.Path));
                return;
            }

            var choices = ChooseIdentityRepairs(review);
            if (choices is null) return;
            var (keepers, legacyRows) = choices.Value;
            var choice = MessageBox.Show(
                "「はい」で原本を別ファイルに退避してから修復を反映します。「いいえ」で修復版を別ファイルに保存します。",
                "重複プロジェクト ID の修復", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.No);
            if (choice == MessageBoxResult.Cancel) return;
            //CSJ,variable=apply,適用
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

    private async Task<bool> RepairPhysicalRegistrationsAsync(JsonProjectRepository json,
        //CSJ,parameter=review,レビュー
        ProjectListIdentityRecovery.PhysicalReview review, CancellationToken token)
    {
        var keepers = ChoosePhysicalRegistrations(review);
        if (keepers is null) return false;

        var choice = MessageBox.Show(
            "「はい」で一覧原本を別ファイルに退避して、選択した登録だけを残します。「いいえ」で修復版を別ファイルへ保存します。",
            "同じファイルを指す登録の修復", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.No);
        if (choice == MessageBoxResult.Cancel) return false;

        //CSJ,variable=apply,適用
        var apply = choice == MessageBoxResult.Yes;
        var picker = new SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json",
            DefaultExt = ".json",
            FileName = apply ? "projects.original.json" : "projects.recovered.json",
            Title = apply ? "一覧原本の退避先を選択" : "修復した一覧のコピーを保存",
            OverwritePrompt = false,
        };
        if (picker.ShowDialog() != true || HasUnsavedProjectList) return false;

        if (apply)
            await ProjectListIdentityRecovery.ApplyPhysicalAsync(json, review, keepers, picker.FileName, token);
        else
            await ProjectListIdentityRecovery.ExportPhysicalRepairAsync(json, review, keepers, picker.FileName, token);

        AssetStatus = (apply ? "選択した登録を残しました。原本の退避先" : "修復した一覧の保存先") + $"\n{picker.FileName}";
        logger.Info($"Physical project registration repair finished. groups={review.Conflicts.Count} applied={apply}");
        return apply;
    }

    private static Dictionary<(ulong Volume, Guid FileId), int>? ChoosePhysicalRegistrations(
        //CSJ,parameter=review,レビュー
        ProjectListIdentityRecovery.PhysicalReview review)
    {
        //CSJ,variable=panel,パネル
        var panel = new DockPanel { Margin = new Thickness(12) };
        var instructions = new TextBlock
        {
            Text = "同じ実ファイルを指すグループごとに、一覧に残す登録を選んでください。外す登録の ID・リンク・関連付けは元の一覧に残ります。プロジェクトとメタデータのファイルはそのままです。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        DockPanel.SetDock(instructions, Dock.Top);
        panel.Children.Add(instructions);

        //CSJ,variable=confirm,確認
        var confirm = new Button { Content = "選択を確認して続ける", IsEnabled = false, IsDefault = true, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(confirm, Dock.Bottom);
        panel.Children.Add(confirm);
        var choices = new StackPanel();
        panel.Children.Add(new ScrollViewer { Content = choices, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var selections = new Dictionary<(ulong Volume, Guid FileId), ComboBox>();
        //CSJ,variable=group,グループ
        foreach (var group in review.Conflicts)
        {
            //CSJ,variable=options,オプション
            var options = new ComboBox { Margin = new Thickness(0, 0, 0, 12) };
            choices.Children.Add(new TextBlock { Text = $"同じファイルを指す登録 {group.Entries.Count} 件" });
            foreach (var entry in group.Entries)
            {
                //CSJ,variable=metadata,メタデータ
                var metadata = entry.HasMetadata ? "関連付けあり" : "関連付けなし";
                var id = entry.ProjectId?.ToString("D") ?? "未設定";
                //CSJ,variable=text,テキスト
                var text = $"項目 {entry.Row + 1}  {entry.Path}\nID {id} / {metadata} / リンク {entry.LinkCount} 件";
                options.Items.Add(new ComboBoxItem { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, Tag = entry.Row });
            }
            selections.Add((group.Volume, group.FileId), options);
            options.SelectionChanged += (_, _) => confirm.IsEnabled = selections.Values.All(box => box.SelectedItem is not null);
            choices.Children.Add(options);
        }

        //CSJ,variable=window,ウィンドウ
        var window = new Window { Title = "同じファイルを指すプロジェクト登録", Width = 820, Height = 460, Content = panel, Owner = GetActiveWindow() };
        confirm.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true
            ? selections.ToDictionary(item => item.Key, item => (int)((ComboBoxItem)item.Value.SelectedItem).Tag)
            : null;
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

        //CSJ,variable=confirm,確認
        var confirm = new Button { Content = "選択を確認して続ける", IsEnabled = false, IsDefault = true, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(confirm, Dock.Bottom);
        grid.Children.Add(confirm);
        //CSJ,variable=fields,フィールド一覧
        var fields = new StackPanel();
        grid.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var selections = new Dictionary<Guid, ComboBox>();
        //CSJ,variable=legacyOptions,旧形式オプション
        var legacyOptions = new Dictionary<int, CheckBox>();
        void UpdateConfirmation() => confirm.IsEnabled = selections.Values.All(box => box.SelectedItem is not null) &&
            (selections.Count > 0 || legacyOptions.Values.Any(option => option.IsChecked == true));
        //CSJ,variable=group,グループ
        foreach (var group in review.Conflicts)
        {
            //CSJ,variable=options,オプション
            var options = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
            fields.Children.Add(new TextBlock { Text = group.Id.ToString("D") });
            foreach (var entry in group.Entries)
                options.Items.Add(new ComboBoxItem { Content = entry.Path + (entry.HasMetadata ? "（関連付けあり）" : ""), Tag = entry.Row });
            selections.Add(group.Id, options);
            options.SelectionChanged += (_, _) => UpdateConfirmation();
            fields.Children.Add(options);
        }
        //CSJ,variable=legacy,旧形式
        var legacy = review.LegacyProjects.Where(entry => !entry.HasMetadata).ToArray();
        if (legacy.Length > 0) fields.Children.Add(new TextBlock { Text = "新しい ID を付ける項目" });
        foreach (var entry in legacy)
        {
            //CSJ,variable=option,オプション
            var option = new CheckBox { Content = entry.Path, ToolTip = entry.Path, Margin = new Thickness(0, 2, 0, 2) };
            legacyOptions.Add(entry.Row, option);
            option.Checked += (_, _) => UpdateConfirmation();
            option.Unchecked += (_, _) => UpdateConfirmation();
            fields.Children.Add(option);
        }
        //CSJ,variable=window,ウィンドウ
        var window = new Window { Title = "プロジェクト一覧の ID を修復", Width = 760, Height = 420, Content = grid, Owner = GetActiveWindow() };
        confirm.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true
            ? (selections.ToDictionary(item => item.Key, item => (int)((ComboBoxItem)item.Value.SelectedItem).Tag),
                legacyOptions.Where(item => item.Value.IsChecked == true).Select(item => item.Key).ToHashSet())
            : null;
    }
}
