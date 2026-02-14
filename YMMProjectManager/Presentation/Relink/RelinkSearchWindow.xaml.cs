using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Relink;

namespace YMMProjectManager.Presentation.Relink;

public partial class RelinkSearchWindow : Window
{
    private static readonly TimeSpan ProgressUiInterval = TimeSpan.FromMilliseconds(200);

    private readonly FileLogger logger;
    private readonly IReadOnlyList<RelinkRow> targetRows;
    private readonly RelinkSearchService searchService;
    private readonly ObservableCollection<string> searchFolders = [];
    private DateTimeOffset lastProgressUiUpdate = DateTimeOffset.MinValue;
    private CancellationTokenSource? cts;

    public RelinkSearchWindow(IReadOnlyList<RelinkRow> targetRows, FileLogger logger)
    {
        InitializeComponent();
        this.targetRows = targetRows;
        this.logger = logger;
        searchService = new RelinkSearchService(logger);

        FoldersList.ItemsSource = searchFolders;
        Closed += (_, _) =>
        {
            cts?.Cancel();
            cts?.Dispose();
            cts = null;
        };
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

        var folder = dialog.FolderName;
        if (searchFolders.Any(x => string.Equals(x, folder, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        searchFolders.Add(folder);
    }

    private void OnRemoveFolderClick(object sender, RoutedEventArgs e)
    {
        if (FoldersList.SelectedItem is not string folder)
        {
            return;
        }

        searchFolders.Remove(folder);
    }

    private async void OnRunClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (searchFolders.Count == 0)
            {
                MessageBox.Show("探索フォルダを1つ以上追加してください。", "素材再リンク", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            cts?.Cancel();
            cts?.Dispose();
            cts = new CancellationTokenSource();

            logger.Info($"Relink.Search start. rows={targetRows.Count}, folders={searchFolders.Count}");
            logger.Flush();
            ProgressText.Text = "探索を開始します...";
            SummaryText.Text = string.Empty;

            var snapshots = targetRows
                .Select(x => new RelinkSearchInputRow
                {
                    RowIndex = x.RowIndex,
                    TypeHint = x.TypeHint,
                    OriginalPath = x.OriginalPath,
                    FileName = x.FileName,
                    Extension = x.Extension,
                    CurrentStatus = x.Status,
                })
                .ToList();

            var progress = new Progress<RelinkSearchProgressInfo>(p =>
            {
                var now = DateTimeOffset.Now;
                if (now - lastProgressUiUpdate < ProgressUiInterval && p.Done < p.Total)
                {
                    return;
                }

                lastProgressUiUpdate = now;
                var totalText = p.Total > 0 ? p.Total.ToString() : "-";
                ProgressText.Text = $"処理中: {p.CurrentFileName} ({p.Done}/{totalText})";
            });

            var execution = await searchService.ExecuteAsync(snapshots, searchFolders.ToList(), cts.Token, progress);
            await Dispatcher.InvokeAsync(() => ApplyUpdates(execution.Updates));

            var result = execution.Summary;
            SummaryText.Text = $"更新{result.UpdatedCount}件 / 曖昧{result.AmbiguousCount}件 / 未発見{result.NotFoundCount}件 / 失敗{result.FailedCount}件";
            logger.Info(
                $"Relink.Search end. scannedFilePathCount={result.ScannedFilePathCount}, missingCount={result.MissingCount}, updatedCount={result.UpdatedCount}, ambiguousCount={result.AmbiguousCount}, notFoundCount={result.NotFoundCount}, skippedCount={result.SkippedCount}, failedCount={result.FailedCount}");
            logger.Flush();

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                MessageBox.Show(result.ErrorMessage, "素材再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            logger.Info("Relink.Search canceled.");
            logger.Flush();
            ProgressText.Text = "キャンセルされました。";
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Relink.Search failed.");
            logger.Flush();
            MessageBox.Show("探索中にエラーが発生しました。ログを確認してください。", "素材再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplyUpdates(IEnumerable<RelinkRowUpdate> updates)
    {
        var rowMap = targetRows.ToDictionary(x => x.RowIndex);
        foreach (var update in updates)
        {
            if (!rowMap.TryGetValue(update.RowIndex, out var row))
            {
                continue;
            }

            row.Candidates.Clear();
            foreach (var candidate in update.Candidates)
            {
                row.Candidates.Add(candidate);
            }

            row.CandidateCount = update.Candidates.Count;
            row.SelectedCandidate = update.SelectedCandidate;
            row.Status = update.Status;
            row.Message = update.Message;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        cts?.Cancel();
    }
}
