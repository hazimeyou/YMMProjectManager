using System.Collections.ObjectModel;
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
