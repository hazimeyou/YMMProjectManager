using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using YMMProjectManager.Domain.Packaging;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Presentation.ViewModels;

//CSJ,class=ProjectListViewModel,プロジェクト一覧の表示モデル
public sealed partial class ProjectListViewModel
{
    //CSJ,method=PackageProjectAsync,パッケージプロジェクト（非同期）
    private Task PackageProjectAsync(string sourcePath) => ExecuteToolAsync("Bundle creation", async token =>
    {
        if (!TryGetOpenableProjectPath(sourcePath, out var projectPath, out var reason))
        {
            MessageBox.Show(reason, "YMMPX 素材受け渡し", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!await Task.Run(() => bundleService.TryEnsureAvailable(out _), token))
        {
            BundleStatus = bundleService.MissingLibraryMessage;
            MessageBox.Show(BundleStatus, "YMMPX 素材受け渡し", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        //CSJ,variable=location,場所
        var location = MessageBox.Show(
            $"対象PF\n{projectPath}\n\nPFのフォルダー内で、PFが参照する素材を確認します。追加の素材フォルダーも選ぶには「はい」を選択してください。「いいえ」ではPFのフォルダーだけを使います。",
            "YMMPX 素材の確認範囲", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
        if (location is not (MessageBoxResult.Yes or MessageBoxResult.No)) return;
        string[]? additionalRoots = null;
        if (location == MessageBoxResult.Yes)
        {
            //CSJ,variable=folder,フォルダー
            var folder = new OpenFolderDialog { Title = "PFが参照する素材の読み取りを許可するフォルダー" };
            if (folder.ShowDialog() != true) return;
            additionalRoots = [folder.FolderName];
        }

        BundleProgress = 0;
        BundleStatus = "素材名と SHA-256 を確認しています。";
        //CSJ,variable=plan,計画
        var plan = await bundleService.InspectTransferAsync(projectPath, token, additionalRoots);
        //CSJ,variable=selection,選択
        var selection = ConfirmTransferAssets(plan, Path.GetFileNameWithoutExtension(projectPath) + "-素材受け渡し");
        if (selection is null)
        {
            BundleStatus = "YMMPX の作成を取りやめました。";
            return;
        }
        var parent = new OpenFolderDialog { Title = "受け渡し用の新しいフォルダーを作成する場所", InitialDirectory = Path.GetDirectoryName(projectPath) };
        if (parent.ShowDialog() != true)
        {
            BundleStatus = "YMMPX の作成を取りやめました。";
            return;
        }
        //CSJ,variable=outputDirectory,出力ディレクトリ
        var outputDirectory = Path.Combine(parent.FolderName, selection.DirectoryName);
        if (MessageBox.Show($"保存先\n{outputDirectory}\n\nここに新しいフォルダーを作り、YMMPXと照合記録を保存します。保存しますか？",
                "YMMPX 出力の確認", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            BundleStatus = "YMMPX の作成を取りやめました。";
            return;
        }

        BundleStatus = "YMMPX と照合記録を作成しています。";
        //CSJ,variable=progress,進捗
        var progress = new Progress<double>(value => BundleProgress = value * 100d);
        var result = await bundleService.CreateTransferAsync(plan, selection.Approvals, outputDirectory, token, progress);
        if (!result.Success)
        {
            BundleStatus = result.ErrorMessage ?? "YMMPX の作成に失敗しました。";
            MessageBox.Show(BundleStatus, "YMMPX 素材受け渡し", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BundleProgress = 100;
        BundleStatus = $"作成完了 {result.OutputPath}\n同梱 {result.PackagedMaterialCount} 件 / 除外・不足 {result.MissingMaterialCount} 件\n受け渡す際は同じフォルダーの照合記録も一緒に渡してください。";
        MessageBox.Show(BundleStatus, "YMMPX 素材受け渡し", MessageBoxButton.OK, MessageBoxImage.Information);
    });

    //CSJ,class=TransferAssetRow,受け渡し素材行
    private sealed class TransferAssetRow(YmmpxTransferCandidate candidate)
    {
        //CSJ,property=CanInclude,同梱の可否
        public bool CanInclude => candidate.CanInclude;
        //CSJ,property=SourcePath,元のパス
        public string SourcePath => candidate.SourcePath;
        //CSJ,property=OriginalFileName,元のファイル名
        public string OriginalFileName => candidate.OriginalFileName;
        //CSJ,property=Evidence,証跡
        public string Evidence => candidate.CanInclude
            ? $"{candidate.Length:N0} バイト\n{candidate.Sha256}"
            : candidate.ExclusionReason ?? "未確認・同梱不可";
        //CSJ,property=Permission,権限
        public YmmpxTransferPermission Permission { get; set; } = YmmpxTransferPermission.Unconfirmed;
    }

    //CSJ,record=TransferPermissionChoice,受け渡し時の同梱許可の選択
    private sealed record TransferPermissionChoice(YmmpxTransferPermission Value, string Label);

    //CSJ,record=TransferSelection,受け渡し選択
    private sealed record TransferSelection(string DirectoryName, IReadOnlyDictionary<string, YmmpxTransferPermission> Approvals);

    //CSJ,method=ConfirmTransferAssets,受け渡し素材一覧を確認する
    private static TransferSelection? ConfirmTransferAssets(YmmpxTransferPlan plan, string defaultDirectoryName)
    {
        //CSJ,variable=rows,行一覧
        var rows = plan.Assets.Select(asset => new TransferAssetRow(asset)).ToArray();
        //CSJ,variable=window,ウィンドウ
        var window = new Window
        {
            Title = "YMMPX に同梱する素材の確認",
            Width = 1020, Height = 560, MinWidth = 680, MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var owner = GetActiveWindow();
        if (owner is not null) window.Owner = owner;
        //CSJ,variable=panel,パネル
        var panel = new DockPanel { Margin = new Thickness(12) };
        var instruction = new TextBlock
        {
            Text = "再配布を確認済みの素材、または自作・権利保有を申告する素材だけ同梱します。未確認・再配布禁止の素材は除外します。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        };
        DockPanel.SetDock(instruction, Dock.Top);
        panel.Children.Add(instruction);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        var accept = new Button { Content = "同梱内容を確認", MinWidth = 115, Margin = new Thickness(0, 8, 8, 0) };
        buttons.Children.Add(accept);
        buttons.Children.Add(new Button { Content = "キャンセル", MinWidth = 100, IsCancel = true, Margin = new Thickness(0, 8, 0, 0) });

        //CSJ,variable=namePanel,名前パネル
        var namePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(namePanel, Dock.Top);
        panel.Children.Add(namePanel);
        namePanel.Children.Add(new TextBlock { Text = "新しい出力フォルダー名" });
        //CSJ,variable=name,名前
        var name = new TextBox { Text = defaultDirectoryName };
        namePanel.Children.Add(name);
        var grid = new DataGrid
        {
            ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "元素材名", Binding = new Binding(nameof(TransferAssetRow.OriginalFileName)), IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "素材参照", Binding = new Binding(nameof(TransferAssetRow.SourcePath)), IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "サイズ / SHA-256", Binding = new Binding(nameof(TransferAssetRow.Evidence)), IsReadOnly = true });
        var permissionEditor = new Style(typeof(ComboBox));
        permissionEditor.Setters.Add(new Setter(UIElement.IsEnabledProperty, new Binding(nameof(TransferAssetRow.CanInclude))));
        grid.Columns.Add(new DataGridComboBoxColumn
        {
            EditingElementStyle = permissionEditor,
            Header = "再配布許諾",
            ItemsSource = new TransferPermissionChoice[]
            {
                new(YmmpxTransferPermission.Unconfirmed, "未確認"),
                new(YmmpxTransferPermission.Prohibited, "再配布禁止"),
                new(YmmpxTransferPermission.RedistributionAllowed, "再配布許可確認済み"),
                new(YmmpxTransferPermission.OwnWork, "自作・権利保有"),
            },
            SelectedValuePath = nameof(TransferPermissionChoice.Value),
            DisplayMemberPath = nameof(TransferPermissionChoice.Label),
            SelectedValueBinding = new Binding(nameof(TransferAssetRow.Permission)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
        });
        panel.Children.Add(grid);
        accept.Click += (_, _) =>
        {
            if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true)) return;
            var value = name.Text.Trim();
            if (!AssetInventory.IsOrdinaryPathComponent(value))
            {
                MessageBox.Show(window, "有効なフォルダー名を入力してください。", window.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            window.DialogResult = true;
        };
        window.Content = panel;
        return window.ShowDialog() == true
            ? new TransferSelection(name.Text.Trim(), rows.Where(row => row.CanInclude)
                .ToDictionary(row => row.SourcePath, row => row.Permission, StringComparer.Ordinal))
            : null;
    }
}
