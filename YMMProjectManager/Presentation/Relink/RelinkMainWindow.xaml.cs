using System.Collections.ObjectModel;
using System.Windows;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Relink;
using YukkuriMovieMaker.Plugin;

namespace YMMProjectManager.Presentation.Relink;

public partial class RelinkMainWindow : Window
{
    private readonly FileLogger logger;
    private readonly RelinkScanService scanService;
    private readonly RelinkSaveService saveService;
    private readonly TimelineMediaRelinkService timelineMediaRelinkService;
    private readonly ObservableCollection<RelinkRow> rows = [];
    private readonly TimelineToolInfo? timelineInfo;
    private RelinkDocumentContext? context;
    private TimelineRelinkContext? timelineContext;

    public RelinkMainWindow(string ymmpPath, FileLogger logger)
        : this(logger, null, ymmpPath)
    {
    }

    public RelinkMainWindow(TimelineToolInfo timelineInfo, FileLogger logger, string? ymmpPath = null)
        : this(logger, timelineInfo, ymmpPath)
    {
    }

    private RelinkMainWindow(FileLogger logger, TimelineToolInfo? timelineInfo, string? ymmpPath)
    {
        InitializeComponent();
        this.timelineInfo = timelineInfo;
        this.logger = logger;
        scanService = new RelinkScanService(logger);
        saveService = new RelinkSaveService(logger);
        timelineMediaRelinkService = new TimelineMediaRelinkService(logger);
        YmmpPath = ymmpPath;
        DataContext = this;
        Loaded += OnLoaded;
    }

    public string? YmmpPath { get; }
    public ObservableCollection<RelinkRow> Rows => rows;
    private bool IsRuntimeTimelineMode => timelineInfo?.Timeline is not null;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SummaryText.Text = "読み込み中...";
        try
        {
            if (IsRuntimeTimelineMode)
            {
                logger.Info("Relink.Load start. mode=runtime-timeline");
                var (scanContext, result) = timelineMediaRelinkService.Scan(timelineInfo);
                if (scanContext is null)
                {
                    SummaryText.Text = result.ErrorMessage ?? "読み込みに失敗しました。";
                    logger.Info($"Relink.Load failed. mode=runtime-timeline, reason={SummaryText.Text}");
                    return;
                }

                timelineContext = scanContext;
                rows.Clear();
                foreach (var row in scanContext.Rows.Where(x => x.Status != RelinkStatus.Existing))
                {
                    rows.Add(row);
                }

                SummaryText.Text = $"検出: missing={result.MissingCount}件 / failed={result.FailedCount}件";
                logger.Info($"Relink.Load end. mode=runtime-timeline, scanned={result.ScannedFilePathCount}, missing={result.MissingCount}, failed={result.FailedCount}");
                logger.Flush();
                return;
            }

            logger.Info($"Relink.Load start. mode=ymmp-file, ymmp={YmmpPath}");
            var (docContext, docResult) = await scanService.ScanAsync(YmmpPath!, CancellationToken.None);
            if (docContext is null)
            {
                SummaryText.Text = docResult.ErrorMessage ?? "読み込みに失敗しました。";
                logger.Info($"Relink.Load failed. mode=ymmp-file, ymmp={YmmpPath}, reason={SummaryText.Text}");
                return;
            }

            context = docContext;
            rows.Clear();
            foreach (var row in docContext.Rows.Where(x => x.Status != RelinkStatus.Existing))
            {
                rows.Add(row);
            }

            SummaryText.Text = $"検出: missing={docResult.MissingCount}件 / failed={docResult.FailedCount}件";
            logger.Info($"Relink.Load end. mode=ymmp-file, ymmp={YmmpPath}, scanned={docResult.ScannedFilePathCount}, missing={docResult.MissingCount}, failed={docResult.FailedCount}");
            logger.Flush();
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Relink.Load failed. mode={(IsRuntimeTimelineMode ? "runtime-timeline" : "ymmp-file")}, ymmp={YmmpPath ?? "<none>"}");
            logger.Flush();
            SummaryText.Text = "読み込み中にエラーが発生しました。";
            MessageBox.Show("読み込み中にエラーが発生しました。ログを確認してください。", "素材再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenSearchSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (rows.Count == 0)
            {
                return;
            }

            var targetRows = rows.Where(x => x.Status is RelinkStatus.Missing or RelinkStatus.Ambiguous or RelinkStatus.NotFound).ToList();
            logger.Info($"Relink.OpenSearch start. targetRows={targetRows.Count}");
            var window = new RelinkSearchWindow(targetRows, logger)
            {
                Owner = this,
            };
            window.ShowDialog();

            var updated = rows.Count(x => x.Status == RelinkStatus.Updated);
            var ambiguous = rows.Count(x => x.Status == RelinkStatus.Ambiguous);
            var notFound = rows.Count(x => x.Status == RelinkStatus.NotFound);
            var failed = rows.Count(x => x.Status == RelinkStatus.Failed);
            SummaryText.Text = $"更新{updated}件 / 曖昧{ambiguous}件 / 未発見{notFound}件 / 失敗{failed}件";
            logger.Info($"Relink.OpenSearch end. updated={updated}, ambiguous={ambiguous}, notFound={notFound}, failed={failed}");
            logger.Flush();
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Relink.OpenSearch failed.");
            logger.Flush();
            MessageBox.Show("探索設定の表示中にエラーが発生しました。ログを確認してください。", "素材再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (context is null && timelineContext is null)
            {
                return;
            }

            logger.Info($"Relink.Save start. mode={(timelineContext is null ? "ymmp-file" : "runtime-timeline")}, ymmp={YmmpPath ?? "<none>"}, rows={rows.Count}");
            SyncRows();

            if (timelineContext is not null)
            {
                var (success, errorMessage, updatedCount) = timelineMediaRelinkService.Save(timelineContext);
                if (!success)
                {
                    logger.Info($"Relink.Save failed. mode=runtime-timeline, reason={errorMessage}");
                    logger.Flush();
                    MessageBox.Show(errorMessage ?? "保存に失敗しました。ログを確認してください。", "素材再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                SummaryText.Text = updatedCount == 0
                    ? "更新対象はありません。"
                    : $"保存完了（タイムライン反映: {updatedCount}件）";
                logger.Info($"Relink.Save end. mode=runtime-timeline, updated={updatedCount}");
                logger.Flush();
                return;
            }

            var (fileSuccess, fileErrorMessage, backupPath) = await saveService.SaveAsync(context!, CancellationToken.None);
            if (!fileSuccess)
            {
                logger.Info($"Relink.Save failed. mode=ymmp-file, ymmp={YmmpPath}, reason={fileErrorMessage}");
                logger.Flush();
                MessageBox.Show(fileErrorMessage ?? "保存に失敗しました。ログを確認してください。", "素材再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SummaryText.Text = backupPath is null
                ? "更新対象はありません。"
                : $"保存完了（バックアップ: {backupPath}）";
            logger.Info($"Relink.Save end. mode=ymmp-file, ymmp={YmmpPath}, backup={backupPath ?? "<none>"}");
            logger.Flush();
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Relink.Save failed. mode={(timelineContext is null ? "ymmp-file" : "runtime-timeline")}, ymmp={YmmpPath ?? "<none>"}");
            logger.Flush();
            MessageBox.Show("保存中にエラーが発生しました。ログを確認してください。", "素材再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SyncRows()
    {
        var contextRows = context?.Rows.ToDictionary(x => x.RowIndex);
        var timelineRows = timelineContext?.Rows.ToDictionary(x => x.RowIndex);
        foreach (var row in rows)
        {
            if (contextRows is not null && contextRows.TryGetValue(row.RowIndex, out var contextRow))
            {
                contextRow.Status = row.Status;
                contextRow.SelectedCandidate = row.SelectedCandidate;
            }

            if (timelineRows is not null && timelineRows.TryGetValue(row.RowIndex, out var timelineRow))
            {
                timelineRow.Status = row.Status;
                timelineRow.SelectedCandidate = row.SelectedCandidate;
            }
        }
    }
}
