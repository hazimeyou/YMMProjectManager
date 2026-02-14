using System.Collections.ObjectModel;
using System.Windows;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Relink;

namespace YMMProjectManager.Presentation.Relink;

public partial class RelinkMainWindow : Window
{
    private readonly FileLogger logger;
    private readonly RelinkScanService scanService;
    private readonly RelinkSaveService saveService;
    private readonly ObservableCollection<RelinkRow> rows = [];
    private RelinkDocumentContext? context;

    public RelinkMainWindow(string ymmpPath, FileLogger logger)
    {
        InitializeComponent();
        this.logger = logger;
        scanService = new RelinkScanService(logger);
        saveService = new RelinkSaveService(logger);
        YmmpPath = ymmpPath;
        DataContext = this;
        Loaded += OnLoaded;
    }

    public string YmmpPath { get; }
    public ObservableCollection<RelinkRow> Rows => rows;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SummaryText.Text = "読み込み中...";
        try
        {
            logger.Info($"Relink.Load start. ymmp={YmmpPath}");
            var (scanContext, result) = await scanService.ScanAsync(YmmpPath, CancellationToken.None);
            if (scanContext is null)
            {
                SummaryText.Text = result.ErrorMessage ?? "読み込みに失敗しました。";
                logger.Info($"Relink.Load failed. ymmp={YmmpPath}, reason={SummaryText.Text}");
                return;
            }

            context = scanContext;
            rows.Clear();
            foreach (var row in scanContext.Rows.Where(x => x.Status != RelinkStatus.Existing))
            {
                rows.Add(row);
            }

            SummaryText.Text = $"検出: missing={result.MissingCount}件 / failed={result.FailedCount}件";
            logger.Info($"Relink.Load end. ymmp={YmmpPath}, scanned={result.ScannedFilePathCount}, missing={result.MissingCount}, failed={result.FailedCount}");
            logger.Flush();
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Relink.Load failed. ymmp={YmmpPath}");
            logger.Flush();
            SummaryText.Text = "読み込み中にエラーが発生しました。";
            MessageBox.Show("読み込み中にエラーが発生しました。ログを確認してください。", "素材再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenSearchSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (context is null)
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
            if (context is null)
            {
                return;
            }

            logger.Info($"Relink.Save start. ymmp={YmmpPath}, rows={rows.Count}");
            foreach (var row in rows)
            {
                var target = context.Rows.FirstOrDefault(x => x.OriginalPath == row.OriginalPath);
                if (target is not null)
                {
                    target.Status = row.Status;
                    target.SelectedCandidate = row.SelectedCandidate;
                }
            }

            var (success, errorMessage, backupPath) = await saveService.SaveAsync(context, CancellationToken.None);
            if (!success)
            {
                logger.Info($"Relink.Save failed. ymmp={YmmpPath}, reason={errorMessage}");
                logger.Flush();
                MessageBox.Show(errorMessage ?? "保存に失敗しました。ログを確認してください。", "素材再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SummaryText.Text = backupPath is null
                ? "更新対象はありません。"
                : $"保存完了（バックアップ: {backupPath}）";
            logger.Info($"Relink.Save end. ymmp={YmmpPath}, backup={backupPath ?? "<none>"}");
            logger.Flush();
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Relink.Save failed. ymmp={YmmpPath}");
            logger.Flush();
            MessageBox.Show("保存中にエラーが発生しました。ログを確認してください。", "素材再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
