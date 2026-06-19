using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YMMProjectManager.Domain;
using YMMProjectManager.Presentation.ViewModels;

namespace YMMProjectManager.Presentation.Views;

public partial class ProjectListView : UserControl
{
    private bool initialized;

    public ProjectListView()
    {
        InitializeComponent();
        if (DataContext is null)
        {
            DataContext = new ProjectListViewModel();
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized)
        {
            return;
        }

        // 再アクティブ化時にリポジトリ読み込みを繰り返さないよう、初回だけ初期化する。
        initialized = true;
        if (DataContext is ProjectListViewModel vm)
        {
            await vm.InitializeAsync();
        }
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        var hasYmmp = files.Any(x => x.EndsWith(".ymmp", StringComparison.OrdinalIgnoreCase));
        e.Effects = hasYmmp ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not ProjectListViewModel vm)
        {
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        var files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        var ymmpFiles = files.Where(x => x.EndsWith(".ymmp", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (ymmpFiles.Length == 0)
        {
            return;
        }

        await vm.AddProjectsAsync(ymmpFiles);
    }

    private void OnProjectListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }

        var dependencyObject = e.OriginalSource as DependencyObject;
        if (dependencyObject is null)
        {
            return;
        }

        var item = FindAncestor<ListBoxItem>(dependencyObject);
        if (item is not null)
        {
            item.IsSelected = true;
            item.Focus();
            return;
        }

        listBox.SelectedItem = null;
    }

    private async void OnSaveGenerationMenuClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectListViewModel vm)
        {
            return;
        }

        await vm.SaveGenerationAsync();
    }

    private async void OnShowGenerationListMenuClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectListViewModel vm)
        {
            return;
        }

        await vm.ShowGenerationListAsync();
    }

    private async void OnShowGenerationDiagnosticsMenuClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectListViewModel vm)
        {
            return;
        }

        await vm.ShowGenerationDiagnosticsAsync();
    }

    private void OnProjectListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }

        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (!IsFromListBoxItem(source, listBox))
        {
            return;
        }

        if (DataContext is ProjectListViewModel vm && vm.OpenCommand.CanExecute(null))
        {
            vm.OpenCommand.Execute(null);
        }
    }

    private static bool IsFromListBoxItem(DependencyObject source, ListBox listBox)
    {
        // クリック元がプロジェクト行のときだけダブルクリックを有効にするため、ツリーを上へたどる。
        var current = source;
        while (current is not null)
        {
            if (current is ListBoxItem)
            {
                return true;
            }

            if (ReferenceEquals(current, listBox))
            {
                return false;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static T? FindAncestor<T>(DependencyObject child)
        where T : DependencyObject
    {
        var current = child;
        while (current is not null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
