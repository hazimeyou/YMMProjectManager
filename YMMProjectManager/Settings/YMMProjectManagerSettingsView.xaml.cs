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
        SummaryText.Text = settings.SetSearchFolders(folders)
            ? "保存しました。"
            : "保存に失敗しました。";
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        ReloadFolders();
        SummaryText.Text = "設定を再読込しました。";
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
