using System.Collections.ObjectModel;
using System.IO;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Output;
using YukkuriMovieMaker.Commons;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace YMMProjectManager.Settings;

//CSJ,class=YMMProjectManagerSettingsView,プロジェクト管理の設定画面
public partial class YMMProjectManagerSettingsView : UserControl
{
    //CSJ,field=settings,設定
    private readonly YMMProjectManagerSettings settings;
    //CSJ,field=folders,フォルダー一覧
    private readonly ObservableCollection<string> folders = [];

    public YMMProjectManagerSettingsView(YMMProjectManagerSettings settings)
    {
        this.settings = settings;
        InitializeComponent();

        FoldersList.ItemsSource = folders;
        ReloadFolders();
    }

    //CSJ,method=OnAddFolderClick,追加フォルダークリック時の処理
    private void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        //CSJ,variable=dialog,ダイアログ
        var dialog = new OpenFolderDialog
        {
            Title = "探索フォルダを選択",
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }

        if (folders.Any(x => string.Equals(x, dialog.FolderName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        folders.Add(dialog.FolderName);
        SummaryText.Text = string.Empty;
    }

    //CSJ,method=OnRemoveFolderClick,除去フォルダークリック時の処理
    private void OnRemoveFolderClick(object sender, RoutedEventArgs e)
    {
        if (FoldersList.SelectedItem is not string folder)
        {
            return;
        }

        folders.Remove(folder);
        SummaryText.Text = string.Empty;
    }

    //CSJ,method=OnSaveClick,保存クリック時の処理
    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (settings.SetSearchFolders(folders))
            SummaryText.Text = "保存しました。";
        else if (settings.RetainedBackupPath is { } backup)
            SummaryText.Text = $"保存に失敗しました。以前の設定を保全しました。\n{backup}";
        else
            SummaryText.Text = "保存に失敗しました。";
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        if (settings.TryReload())
        {
            ReloadFolders();
            SummaryText.Text = "設定を再読込しました。";
        }
        else if (settings.FindInterruptedSave() is { } candidate)
        {
            var confirmation = MessageBox.Show(
                $"中断前の設定を確認できました。以前の設定を復元しますか？\n{candidate.BackupPath}",
                "設定の復元", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                SummaryText.Text = "復元を見送りました。以前の設定は保全されています。";
                return;
            }

            if (settings.TryRecoverInterruptedSave(candidate))
            {
                ReloadFolders();
                SummaryText.Text = "中断前の設定を復元しました。";
            }
            else
            {
                SummaryText.Text = $"以前の設定を復元できませんでした。退避先を確認してください。\n{candidate.BackupPath}";
            }
        }
        else
        {
            SummaryText.Text = settings.RetainedBackupPath is { } backup
                ? $"設定を再読込できませんでした。退避先を確認してください。\n{backup}"
                : "設定の再読込に失敗しました。";
        }
    }

    private static string FilmstripRoot =>
        Path.Combine(AppDirectories.UserDirectory, "plugin", "YMMProjectManager", "cache", "filmstrip");

    private async void OnRefreshStorageClick(object sender, RoutedEventArgs e) =>
        await RefreshStorageAsync(null);

    private async void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("所有を確認できる完了キャッシュを削除しますか？\n旧キャッシュと中断ファイルは保持します。", "キャッシュの削除",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await RefreshStorageAsync(() => ThumbnailCacheMaintenance.Trim(FilmstripRoot, 0, deleteAll: true));
    }

    private async void OnClearLogsClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("ローテーション済みの診断ログを削除しますか？", "過去ログの削除",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await RefreshStorageAsync(PluginDiagnostics.Logger.DeleteArchives);
    }

    private async Task RefreshStorageAsync(Func<int>? cleanup)
    {
        MaintenanceActions.IsEnabled = false;
        try
        {
            var root = FilmstripRoot;
            var result = await Task.Run(() =>
            {
                var deleted = cleanup?.Invoke();
                var cache = ThumbnailCacheMaintenance.InspectUsage(root);
                var logs = PluginDiagnostics.Logger.InspectLogs();
                return (deleted, cache, logs);
            });
            var operation = result.deleted is int count ? $"削除 {count:N0} 件。\n" : "";
            StorageSummary.Text = operation +
                $"確認済みキャッシュ {result.cache.ManagedCount:N0} 件 / {result.cache.ManagedBytes:N0} バイト\n" +
                $"旧形式・未照合 {result.cache.UnverifiedBytes:N0} バイト / 中断物 {result.cache.InterruptedBytes:N0} バイト\n" +
                (result.cache.Skipped > 0 ? $"確認できなかった項目 {result.cache.Skipped:N0} 件\n" : "") +
                $"診断ログ {result.logs.Bytes:N0} バイト（過去ログ {result.logs.Archives:N0} 件）";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StorageSummary.Text = "対象を確認できませんでした。開いているファイルや保存先を確認してください。";
        }
        finally
        {
            MaintenanceActions.IsEnabled = true;
        }
    }

    private void ReloadFolders()
    {
        folders.Clear();
        //CSJ,variable=folder,フォルダー
        foreach (var folder in settings.GetSearchFolders())
        {
            folders.Add(folder);
        }
    }
}
