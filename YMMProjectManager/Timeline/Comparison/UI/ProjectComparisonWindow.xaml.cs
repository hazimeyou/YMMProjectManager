using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using YMMProjectManager.Application.PreviewComparison;

namespace YMMProjectManager.Presentation.Views;

public partial class ProjectComparisonWindow : Window
{
    private readonly ProjectComparisonWorkspace workspace;
    private CancellationTokenSource? settingsCancellation;
    private bool updatingSeek;
    private bool closed;

    public ProjectComparisonWindow() : this(new ProjectComparisonWorkspace()) { }

    internal ProjectComparisonWindow(ProjectComparisonWorkspace workspace)
    {
        this.workspace = workspace;
        InitializeComponent();
        DataContext = workspace;
        DisconnectedNotice.Visibility = workspace.IsRendererConnected ? Visibility.Collapsed : Visibility.Visible;
        workspace.PropertyChanged += OnWorkspaceChanged;
        UpdateControls();
    }

    private void OnSelectOriginalClick(object sender, RoutedEventArgs e) => PickProject(PreviewComparisonSide.A);
    private void OnSelectCurrentClick(object sender, RoutedEventArgs e) => PickProject(PreviewComparisonSide.B);

    private void PickProject(PreviewComparisonSide side)
    {
        var picker = new OpenFileDialog
        {
            Title = side == PreviewComparisonSide.A ? "元プロジェクトを選択" : "現プロジェクトを選択",
            Filter = "YMMプロジェクト (*.ymmp)|*.ymmp",
            CheckFileExists = true,
        };
        if (picker.ShowDialog(this) == true)
            ApplyEdit(() => workspace.SetProject(side, picker.FileName));
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e) => UpdateControls();

    private void UpdateControls()
    {
        updatingSeek = true;
        try
        {
            CommonSeekBar.Maximum = workspace.SeekMaximumSeconds;
            CommonSeekBar.Value = workspace.CommonSeconds;
            SectionList.ItemsSource = workspace.Sections;
            RefreshPreviewButton.IsEnabled = workspace.CanRender;
            CancelPreviewButton.IsEnabled = workspace.CanCancel || settingsCancellation is { IsCancellationRequested: false };
            UpdateSectionButtons();
        }
        finally { updatingSeek = false; }
    }

    private void OnCommonSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updatingSeek || CommonTimeInput is null) return;
        workspace.CommonSeconds = e.NewValue;
        CommonTimeInput.Text = workspace.CommonTimeLabel;
        EditStatus.Text = "";
    }

    private void OnApplyTimeClick(object sender, RoutedEventArgs e) =>
        ApplyEdit(() =>
        {
            workspace.SetCommonTime(ReadTime(CommonTimeInput.Text, nonnegative: true));
            CommonTimeInput.Text = workspace.CommonTimeLabel;
        });

    private void OnApplyFixedClick(object sender, RoutedEventArgs e) =>
        ApplyEdit(() =>
        {
            workspace.SetDefaultOffset(ReadTime(FixedOffsetInput.Text));
            FixedOffsetInput.Text = ProjectComparisonWorkspace.FormatTime(workspace.DefaultOffset, signed: true);
        });

    private void OnSectionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedItem is ComparisonOffset previous)
        {
            SectionStartInput.Text = ProjectComparisonWorkspace.FormatTime(previous.Start);
            SectionOffsetInput.Text = ProjectComparisonWorkspace.FormatTime(previous.Offset, signed: true);
        }
        UpdateSectionButtons();
    }

    private void UpdateSectionButtons()
    {
        if (EditSectionButton is null || RemoveSectionButton is null) return;
        EditSectionButton.IsEnabled = RemoveSectionButton.IsEnabled = SectionList.SelectedItem is ComparisonOffset;
    }

    private void OnAddSectionClick(object sender, RoutedEventArgs e) => SaveSection(null);
    private void OnEditSectionClick(object sender, RoutedEventArgs e)
    {
        if (SectionList.SelectedItem is ComparisonOffset previous) SaveSection(previous);
    }

    private void SaveSection(ComparisonOffset? previous) =>
        ApplyEdit(() =>
        {
            var section = new ComparisonOffset(ReadTime(SectionStartInput.Text, nonnegative: true),
                ReadTime(SectionOffsetInput.Text));
            workspace.SetSection(previous, section);
            SectionList.SelectedItem = section;
        });

    private void OnRemoveSectionClick(object sender, RoutedEventArgs e)
    {
        if (SectionList.SelectedItem is ComparisonOffset selected)
            ApplyEdit(() => workspace.RemoveSection(selected));
    }

    private async void OnRefreshPreviewClick(object sender, RoutedEventArgs e)
    {
        EditStatus.Text = "";
        await workspace.RefreshPreviewAsync();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (workspace.CanCancel) workspace.CancelPreview();
        settingsCancellation?.Cancel();
        UpdateControls();
    }

    private async void OnSaveTimingClick(object sender, RoutedEventArgs e)
    {
        if (settingsCancellation is not null) return;
        var picker = new SaveFileDialog
        {
            Title = "比較設定を新規ファイルに保存",
            Filter = "比較設定 (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = false,
        };
        if (picker.ShowDialog(this) == true)
            await SaveTimingAsync(picker.FileName);
    }

    private async void OnLoadTimingClick(object sender, RoutedEventArgs e)
    {
        if (settingsCancellation is not null) return;
        var picker = new OpenFileDialog
        {
            Title = "比較設定を読み込む",
            Filter = "比較設定 (*.json)|*.json",
            CheckFileExists = true,
        };
        if (picker.ShowDialog(this) == true)
            await LoadTimingAsync(picker.FileName);
    }

    internal Task SaveTimingAsync(string path) =>
        RunFileOperationAsync(token => workspace.SaveTimingCopyAsync(path, token), "比較設定を保存しました。");

    internal Task LoadTimingAsync(string path) =>
        RunFileOperationAsync(async token =>
        {
            await workspace.LoadTimingAsync(path, token);
            if (!closed) FixedOffsetInput.Text = ProjectComparisonWorkspace.FormatTime(workspace.DefaultOffset, signed: true);
        }, "比較設定を読み込みました。");

    private async Task RunFileOperationAsync(Func<CancellationToken, Task> operation, string success)
    {
        using var cancellation = new CancellationTokenSource();
        settingsCancellation = cancellation;
        UpdateControls();
        EditStatus.Text = "";
        try
        {
            await operation(cancellation.Token);
            if (!closed) EditStatus.Text = success;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!closed) EditStatus.Text = "設定の操作を中止しました。";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            if (!closed) EditStatus.Text = "設定を処理できませんでした。ファイル形式・保存先・同名ファイルを確認してください。";
        }
        finally
        {
            settingsCancellation = null;
            if (!closed) UpdateControls();
        }
    }

    private void ApplyEdit(Action apply)
    {
        try
        {
            apply();
            EditStatus.Text = "";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            EditStatus.Text = ex is InvalidDataException or InvalidOperationException
                ? ex.Message : "選択したPFのパスを確認してください。";
        }
    }

    private static TimeSpan ReadTime(string input, bool nonnegative = false)
    {
        if (!ProjectComparisonWorkspace.TryReadTime(input, out var time) || (nonnegative && time < TimeSpan.Zero))
            throw new InvalidDataException(nonnegative ? "時刻は 00:00:00 以上の hh:mm:ss で入力してください。"
                : "オフセットは +00:00:10 や -00:00:05 の形式で入力してください。");
        return time;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        closed = true;
        workspace.PropertyChanged -= OnWorkspaceChanged;
        settingsCancellation?.Cancel();
        workspace.Dispose();
    }
}
