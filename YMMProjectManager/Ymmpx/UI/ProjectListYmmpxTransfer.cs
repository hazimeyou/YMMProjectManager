using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using YMMProjectManager.Domain.Packaging;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Packaging;

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
            await OfferYmmpxLibDownloadAsync(token);
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
        var (plan, reviewedRules) = await Task.Run(async () =>
        {
            var rules = await LoadYmmpxRulesAsync(projectPath, token);
            var preview = await bundleService.InspectTransferAsync(projectPath, token, additionalRoots,
                YmmpxExclusionRules.Merge(rules.Global.Rules, rules.Local.Rules));
            return (preview, rules);
        }, token);

        var selection = ConfirmTransferAssets(plan);
        if (selection is null)
        {
            BundleStatus = "YMMPX の作成を取りやめました。";
            return;
        }

        var output = new SaveFileDialog
        {
            Title = "新しい YMMPX の保存先",
            Filter = "YMM同梱ファイル (*.ymmpx)|*.ymmpx",
            FileName = Path.GetFileNameWithoutExtension(projectPath) + "-素材受け渡し.ymmpx",
            DefaultExt = ".ymmpx",
            InitialDirectory = Path.GetDirectoryName(projectPath),
            OverwritePrompt = false,
        };
        if (output.ShowDialog() != true)
        {
            BundleStatus = "YMMPX の作成を取りやめました。";
            return;
        }

        BundleStatus = "標準 YMMPX を作成しています。";
        var progress = new Progress<double>(value => BundleProgress = value * 100d);
        var result = await bundleService.CreateStandardPackageAsync(plan, output.FileName,
            selection.IncludeUiSettings, selection.AcceptUnavailable, token, progress,
            verifyToken => YmmpxExclusionRules.PinReviewedAsync(
                [(YmmpxGlobalExclusionPath, reviewedRules.Global), (YmmpxLocalExclusionPath(projectPath), reviewedRules.Local)], verifyToken));
        if (!result.Success)
        {
            BundleStatus = result.ErrorMessage ?? "YMMPX の作成に失敗しました。";
            MessageBox.Show(BundleStatus, "YMMPX 素材受け渡し", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BundleProgress = 100;
        int excluded = plan.Assets.Count(asset => asset.Disposition == YmmpxMaterialDisposition.Excluded);
        BundleStatus = $"作成完了 {result.OutputPath}\n同梱 {result.PackagedMaterialCount} 件 / 除外 {excluded} 件 / 不足 {result.MissingMaterialCount} 件";
        MessageBox.Show(BundleStatus, "YMMPX 素材受け渡し", MessageBoxButton.OK, MessageBoxImage.Information);
    });

    public ICommand ImportYmmpxExclusionsCommand { get; }

    private static string YmmpxGlobalExclusionPath => Path.Combine(
        Path.GetDirectoryName(typeof(ProjectListViewModel).Assembly.Location)!, YmmpxExclusionRules.GlobalFileName);

    private static string YmmpxLocalExclusionPath(string projectPath) =>
        Path.Combine(Path.GetDirectoryName(projectPath)!, YmmpxExclusionRules.GetLocalName(projectPath));

    private static async Task<(YmmpxExclusionRules.Snapshot Global, YmmpxExclusionRules.Snapshot Local)> LoadYmmpxRulesAsync(
        string projectPath, CancellationToken token) => (
        await YmmpxExclusionRules.LoadAsync(YmmpxGlobalExclusionPath, token, missingIsEmpty: true),
        await YmmpxExclusionRules.LoadAsync(YmmpxLocalExclusionPath(projectPath), token, missingIsEmpty: true));

    private Task ImportYmmpxExclusionsAsync() => ExecuteToolAsync("Bundle exclusion import", async token =>
    {
        var decision = MessageBox.Show(
            "選択したPFだけの設定に取り込むには「はい」、PMの全体設定に取り込むには「いいえ」を選んでください。",
            "YMMPX 除外設定の取込先", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (decision is not (MessageBoxResult.Yes or MessageBoxResult.No)) return;
        var selected = SelectedProject?.FullPath;
        if (decision == MessageBoxResult.Yes && string.IsNullOrWhiteSpace(selected))
        {
            MessageBox.Show("PFを一覧で選択してください。", "YMMPX 除外設定", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var destination = decision == MessageBoxResult.Yes ? YmmpxLocalExclusionPath(selected!) : YmmpxGlobalExclusionPath;
        var source = new OpenFileDialog
        {
            Title = "Packager の exclude.json または PF別の .exclude.json",
            Filter = "除外設定 (*.json)|*.json",
            CheckFileExists = true,
        };
        if (source.ShowDialog() != true) return;
        if (string.Equals(Path.GetFullPath(source.FileName), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("取り込み元と反映先は別のファイルを指定してください。");

        var (imported, current) = await Task.Run(async () =>
            (await YmmpxExclusionRules.LoadAsync(source.FileName, token),
             await YmmpxExclusionRules.LoadAsync(destination, token, missingIsEmpty: true)), token);
        var merged = YmmpxExclusionRules.Merge(current.Rules, imported.Rules);
        if (!ConfirmExclusionImport(destination, imported.Rules, merged)) return;
        await Task.Run(() => YmmpxExclusionRules.ImportAsync(destination, current, merged, token), token);
        BundleStatus = $"PMの除外設定に取り込みました（{merged.Count} 件）。";
    });

    private static bool ConfirmExclusionImport(string destination, IReadOnlyList<YmmpxExclusionRule> imported,
        IReadOnlyList<YmmpxExclusionRule> merged)
    {
        var window = new Window { Title = "YMMPX 除外設定の確認", Width = 800, Height = 570,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        if (GetActiveWindow() is { } owner) window.Owner = owner;
        var panel = new DockPanel { Margin = new Thickness(12) };
        var text = new TextBlock { Text = $"PMの反映先\n{destination}\n同じパスと種類のルールは取り込み元を優先します。Packagerの設定ファイルは変更されません。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(text, Dock.Top);
        panel.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        var accept = new Button { Content = "反映する", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 8, 8, 0) };
        accept.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(accept);
        buttons.Children.Add(new Button { Content = "キャンセル", IsCancel = true, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 8, 0, 0) });
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = $"取り込み元 ({imported.Count})", Content = BuildRules(imported) });
        tabs.Items.Add(new TabItem { Header = $"反映後 ({merged.Count})", Content = BuildRules(merged) });
        panel.Children.Add(tabs);
        window.Content = panel;
        return window.ShowDialog() == true;

        static DataGrid BuildRules(IReadOnlyList<YmmpxExclusionRule> rules)
        {
            var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, ItemsSource = rules };
            grid.Columns.Add(new DataGridTextColumn { Header = "パス", Binding = new Binding(nameof(YmmpxExclusionRule.Path)) });
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = "フォルダー", Binding = new Binding(nameof(YmmpxExclusionRule.IsFolder)) });
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = "除外する", Binding = new Binding(nameof(YmmpxExclusionRule.IsExcluded)) });
            return grid;
        }
    }


    private async Task OfferYmmpxLibDownloadAsync(CancellationToken token)
    {
        var guide = new YmmpxLibInstallGuide(logger);
        BundleStatus = bundleService.MissingLibraryMessage;
        var instructions = guide.BuildGuide(BundleStatus, bundleService.SearchedPaths, guide.FindLegacyFolders());
        if (MessageBox.Show(instructions + "\n\n対応する公式版の保存先を選びますか？",
            "YmmpxLibPlugin の導入・更新", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var destination = new SaveFileDialog
        {
            Title = "YmmpxLibPlugin の保存先",
            Filter = "YMM4 プラグイン (*.ymme)|*.ymme",
            FileName = YmmpxLibInstallGuide.SuggestedFileName,
            DefaultExt = ".ymme",
            OverwritePrompt = false,
        };
        if (destination.ShowDialog() != true) return;
        var destinationPath = destination.FileName;

        var approved = MessageBox.Show(
            $"公式 {YmmpxLibInstallGuide.SupportedRelease} の .ymme を取得し、SHA-256 を確認して次の場所に保存します。\n\n{destinationPath}\n\nダウンロードしますか？",
            "YmmpxLibPlugin の取得確認", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        if (approved) BundleStatus = "YmmpxLibPlugin を取得しています。";
        YmmpxLibInstallResult? result;
        try
        {
            result = await Task.Run(() => guide.DownloadInstallerAsync(destinationPath, approved, token), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            BundleStatus = "YmmpxLibPlugin の取得を取り消しました。";
            throw;
        }
        if (result is null)
        {
            BundleStatus = "YmmpxLibPlugin の取得を取りやめました。";
            return;
        }

        BundleStatus = result.Success
            ? $"公式配布物を確認して保存しました。\n{result.DownloadPath}\n保存した .ymme を YMM4 の手順で導入し、YMM4 を再起動してください。"
            : result.ErrorMessage ?? "YmmpxLibPlugin を取得できませんでした。";
        MessageBox.Show(BundleStatus, "YmmpxLibPlugin", MessageBoxButton.OK,
            result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private sealed record TransferSelection(bool IncludeUiSettings, bool AcceptUnavailable);
    private sealed record TransferAssetRow(string OriginalFileName, string SourcePath, string Evidence, string Disposition);

    private static TransferSelection? ConfirmTransferAssets(YmmpxTransferPlan plan)
    {
        var rows = plan.Assets.Select(asset => new TransferAssetRow(asset.OriginalFileName, asset.SourcePath,
            asset.Reason ?? $"{asset.Length:N0} バイト / {asset.Sha256}",
            asset.Disposition switch
            {
                YmmpxMaterialDisposition.Included => "同梱",
                YmmpxMaterialDisposition.Excluded => "設定で除外",
                _ => "不足・読み取り不可",
            })).ToArray();
        var unavailable = plan.Assets.Count(asset => asset.Disposition == YmmpxMaterialDisposition.Unavailable);
        var window = new Window
        {
            Title = "YMMPX に同梱する素材の確認",
            Width = 1020, Height = 560, MinWidth = 680, MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        if (GetActiveWindow() is { } owner) window.Owner = owner;
        var panel = new DockPanel { Margin = new Thickness(12) };
        var included = plan.Assets.Count(asset => asset.Disposition == YmmpxMaterialDisposition.Included);
        var heading = new TextBlock
        {
            Text = $"同梱 {included} 件 / 設定で除外 {rows.Length - included - unavailable} 件 / 不足・読み取り不可 {unavailable} 件",
            Margin = new Thickness(0, 0, 0, 6),
        };
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        var ui = new CheckBox { Content = "プロジェクトの UI 状態を同梱する", IsChecked = true, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(ui, Dock.Top);
        panel.Children.Add(ui);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        var accept = new Button { Content = "保存先を選ぶ", MinWidth = 120, Margin = new Thickness(0, 8, 8, 0) };
        buttons.Children.Add(accept);
        buttons.Children.Add(new Button { Content = "キャンセル", MinWidth = 100, IsCancel = true, Margin = new Thickness(0, 8, 0, 0) });
        var grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, IsReadOnly = true };
        grid.Columns.Add(new DataGridTextColumn { Header = "状態", Binding = new Binding(nameof(TransferAssetRow.Disposition)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "元素材名", Binding = new Binding(nameof(TransferAssetRow.OriginalFileName)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "素材参照", Binding = new Binding(nameof(TransferAssetRow.SourcePath)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "確認結果", Binding = new Binding(nameof(TransferAssetRow.Evidence)) });
        panel.Children.Add(grid);
        accept.Click += (_, _) =>
        {
            if (unavailable > 0 && MessageBox.Show(window, $"表示された不足・読み取り不可の素材 {unavailable} 件を含めずに続行しますか？",
                window.Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            window.DialogResult = true;
        };
        window.Content = panel;
        return window.ShowDialog() == true ? new(ui.IsChecked == true, AcceptUnavailable: unavailable > 0) : null;
    }
}
