using System.Windows;
using System.Windows.Controls;
using YMMProjectManager.Application;

namespace YMMProjectManager.Presentation.Views;

//CSJ,class=ProjectListConflictReview,プロジェクト一覧競合レビュー
internal sealed class ProjectListConflictReview : Window
{
    private readonly ListBox conflicts = new();
    //CSJ,field=local,ローカル
    private readonly TextBox local = ValueBox();
    //CSJ,field=remote,リモート
    private readonly TextBox remote = ValueBox();
    //CSJ,field=confirm,確認
    private readonly Button confirm = new() { Content = "選択した内容を確認", IsDefault = true, IsEnabled = false, MinWidth = 130 };
    private readonly Dictionary<ProjectListConflict, bool> decisions = [];

    internal ProjectListConflictReview(IReadOnlyList<ProjectListConflict> alternatives, Window? owner)
    {
        Title = "一覧の競合を解決";
        Width = 960;
        Height = 570;
        MinWidth = 660;
        MinHeight = 350;
        Owner = owner;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;

        //CSJ,variable=panel,パネル
        var panel = new DockPanel { Margin = new Thickness(12) };
        var instructions = new TextBlock
        {
            Text = "各項目の両方の値を確認し、残す値を選んでください。",
            Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(instructions, Dock.Top);
        panel.Children.Add(instructions);
        //CSJ,variable=actions,アクション一覧
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        //CSJ,variable=cancel,キャンセル
        var cancel = new Button { Content = "キャンセル", IsCancel = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        confirm.Click += (_, _) => DialogResult = true;
        actions.Children.Add(confirm);
        actions.Children.Add(cancel);
        DockPanel.SetDock(actions, Dock.Bottom);
        panel.Children.Add(actions);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        //CSJ,variable=details,詳細
        var details = new Grid { Margin = new Thickness(10, 0, 0, 0) };
        details.RowDefinitions.Add(new RowDefinition());
        details.RowDefinitions.Add(new RowDefinition());
        details.Children.Add(Alternative("未保存の値", local, true));
        var disk = Alternative("ディスク上の値", remote, false);
        Grid.SetRow(disk, 1);
        details.Children.Add(disk);
        Grid.SetColumn(details, 1);
        columns.Children.Add(conflicts);
        columns.Children.Add(details);
        panel.Children.Add(columns);
        Content = panel;

        foreach (var item in alternatives)
            conflicts.Items.Add(new ListBoxItem { Tag = item, Content = Label(item), ToolTip = item.Item });
        conflicts.SelectionChanged += (_, _) => ShowSelection();
        if (conflicts.Items.Count > 0) conflicts.SelectedIndex = 0;
    }

    internal bool AllDecisionsMade => conflicts.Items.Count > 0 && decisions.Count == conflicts.Items.Count;
    internal IReadOnlyDictionary<ProjectListConflict, bool> Decisions => decisions;

    internal void ChooseCurrent(bool savedLocally)
    {
        if (conflicts.SelectedItem is not ListBoxItem { Tag: ProjectListConflict conflict } row) return;
        decisions[conflict] = savedLocally;
        row.Content = Label(conflict);
        confirm.IsEnabled = AllDecisionsMade;
        var next = conflicts.Items.OfType<ListBoxItem>().FirstOrDefault(item =>
            item.Tag is ProjectListConflict pending && !decisions.ContainsKey(pending));
        if (next is not null)
        {
            conflicts.SelectedItem = next;
            conflicts.ScrollIntoView(next);
        }
    }

    //CSJ,method=Label,ラベル
    private string Label(ProjectListConflict item)
    {
        //CSJ,variable=state,状態
        var state = decisions.TryGetValue(item, out var selected) ? selected ? "未保存" : "ディスク" : "未選択";
        return $"{state}  {item.Field}\n{item.Item}";
    }

    //CSJ,method=ShowSelection,選択を表示する
    private void ShowSelection()
    {
        var item = (conflicts.SelectedItem as ListBoxItem)?.Tag as ProjectListConflict;
        local.Text = item?.Local ?? string.Empty;
        remote.Text = item?.Remote ?? string.Empty;
    }

    private DockPanel Alternative(string label, TextBox value, bool useLocal)
    {
        var alternative = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        //CSJ,variable=button,ボタン
        var button = new Button { Content = $"{label}を残す", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) };
        button.Click += (_, _) => ChooseCurrent(useLocal);
        DockPanel.SetDock(button, Dock.Top);
        alternative.Children.Add(button);
        alternative.Children.Add(value);
        return alternative;
    }

    private static TextBox ValueBox() => new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
}
