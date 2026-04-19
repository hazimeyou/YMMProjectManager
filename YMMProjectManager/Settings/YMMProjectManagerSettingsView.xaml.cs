using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace YMMProjectManager.Settings;

public partial class YMMProjectManagerSettingsView : UserControl
{
    private readonly YMMProjectManagerSettings settings;
    private readonly ObservableCollection<string> folders = [];

    public YMMProjectManagerSettingsView(YMMProjectManagerSettings settings)
    {
        this.settings = settings;
        InitializeComponent();

        FoldersList.ItemsSource = folders;
        ReloadFolders();
    }

    private void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
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

    private void OnRemoveFolderClick(object sender, RoutedEventArgs e)
    {
        if (FoldersList.SelectedItem is not string folder)
        {
            return;
        }

        folders.Remove(folder);
        SummaryText.Text = string.Empty;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        settings.SetSearchFolders(folders);
        SummaryText.Text = "保存しました。";
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        ReloadFolders();
        SummaryText.Text = "設定を再読込しました。";
    }

    private void ReloadFolders()
    {
        folders.Clear();
        foreach (var folder in settings.GetSearchFolders())
        {
            folders.Add(folder);
        }
    }
}
