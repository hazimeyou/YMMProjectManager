using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Relink;
using YMMProjectManager.Settings;
using YukkuriMovieMaker.Plugin;

namespace YMMProjectManager.Presentation.Relink;

public partial class RelinkMainWindow : Window
{
    private static readonly TimeSpan ProgressUiInterval = TimeSpan.FromMilliseconds(200);

    private readonly FileLogger logger;
    private readonly RelinkScanService scanService;
    private readonly RelinkSearchService searchService;
    private readonly RelinkSaveService saveService;
    private readonly TimelineMediaRelinkService timelineMediaRelinkService;
    private readonly ObservableCollection<RelinkRow> rows = [];
    private readonly TimelineToolInfo? timelineInfo;
    private readonly Guid? projectId;
    private readonly Func<CancellationToken, Task<AssetRevisionSeries.BranchSelection>>? resolveMetadata;
    private readonly string? metadataDirectory;
    private RelinkDocumentContext? context;
    private TimelineRelinkContext? timelineContext;
    private DateTimeOffset lastProgressUiUpdate = DateTimeOffset.MinValue;
    private CancellationTokenSource? searchCts;
    private bool isSearchRunning;
    private CancellationTokenSource? saveCancellation;
    private bool suppressUiAfterClose;

    public RelinkMainWindow(string ymmpPath, FileLogger logger, Guid? projectId = null,
        Func<CancellationToken, Task<AssetRevisionSeries.BranchSelection>>? resolveMetadata = null, string? metadataDirectory = null)
        : this(logger, null, ymmpPath)
    {
        this.projectId = projectId;
        this.resolveMetadata = resolveMetadata;
        this.metadataDirectory = metadataDirectory;
        if (resolveMetadata is not null && metadataDirectory is not null)
            RecoverMetadataButton.Visibility = Visibility.Visible;
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
        searchService = new RelinkSearchService(logger);
        saveService = new RelinkSaveService(logger);
        timelineMediaRelinkService = new TimelineMediaRelinkService(logger);
        YmmpPath = ymmpPath;
        DataContext = this;
        Loaded += OnLoaded;
        Closing += OnWindowClosing;
    }

    public string? YmmpPath { get; }
    internal Func<AssetRelinkRecovery.OutputReceipt, CancellationToken, Task>? TransferAssociationAsync { get; init; }
    internal YMMProjectManager.Domain.ProjectFileIdentity? ExpectedSourceIdentity { get; init; }
    public ObservableCollection<RelinkRow> Rows => rows;
    private bool IsRuntimeTimelineMode => timelineInfo?.Timeline is not null;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetSummaryText("読み込み中...");
        SetSearchUiState(isRunning: false);
        try
        {
            if (IsRuntimeTimelineMode)
            {
                logger.Info("Relink.Load start. mode=runtime-timeline");
                var (scanContext, result) = timelineMediaRelinkService.Scan(timelineInfo);
                if (scanContext is null)
                {
                    SetSummaryText(result.ErrorMessage ?? "読み込みに失敗しました。");
                    logger.Info($"Relink.Load failed. mode=runtime-timeline, reason={result.ErrorMessage}");
                    return;
                }

                timelineContext = scanContext;
                rows.Clear();
                foreach (var row in scanContext.Rows.Where(x => x.Status != RelinkStatus.Existing))
                {
                    rows.Add(row);
                }

                SetSummaryText($"検出: missing={result.MissingCount}件 / failed={result.FailedCount}件");
                logger.Info($"Relink.Load end. mode=runtime-timeline, scanned={result.ScannedFilePathCount}, missing={result.MissingCount}, failed={result.FailedCount}");
                logger.Flush();
                return;
            }

            logger.Info($"Relink.Load start. mode=ymmp-file, ymmp={YmmpPath}");
            var (docContext, docResult) = await scanService.ScanAsync(YmmpPath!, CancellationToken.None);
            if (docContext is null)
            {
                SetSummaryText(docResult.ErrorMessage ?? "読み込みに失敗しました。");
                logger.Info($"Relink.Load failed. mode=ymmp-file, ymmp={YmmpPath}, reason={docResult.ErrorMessage}");
                return;
            }

            context = docContext;
            rows.Clear();
            foreach (var row in docContext.Rows.Where(x => x.Status != RelinkStatus.Existing))
            {
                rows.Add(row);
            }

            SetSummaryText($"検出: missing={docResult.MissingCount}件 / failed={docResult.FailedCount}件");
            logger.Info($"Relink.Load end. mode=ymmp-file, ymmp={YmmpPath}, scanned={docResult.ScannedFilePathCount}, missing={docResult.MissingCount}, failed={docResult.FailedCount}");
            logger.Flush();
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Relink.Load failed. mode={(IsRuntimeTimelineMode ? "runtime-timeline" : "ymmp-file")}, ymmp={YmmpPath ?? "<none>"}");
            logger.Flush();
            SetSummaryText("読み込み中にエラーが発生しました。");
            if (CanShowUiFeedback())
            {
                MessageBox.Show("読み込み中にエラーが発生しました。ログを確認してください。", "素材再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private async void OnOpenSearchSettingsClick(object sender, RoutedEventArgs e)
    {
        if (isSearchRunning)
        {
            return;
        }

        try
        {
            if (rows.Count == 0)
            {
                return;
            }

            var targetRows = rows
                .Where(x => x.Status is RelinkStatus.Missing or RelinkStatus.Ambiguous or RelinkStatus.NotFound)
                .ToList();
            if (targetRows.Count == 0)
            {
                SetSummaryText("探索対象はありません。");
                return;
            }

            var searchFolders = GetUsableSearchFolders();
            if (searchFolders.Count == 0)
            {
                if (CanShowUiFeedback())
                {
                    MessageBox.Show(
                        "探索フォルダが未設定、または存在しません。設定メニューの「プロジェクトマネージャー」で探索フォルダを追加してください。",
                        "素材再リンク",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                return;
            }

            searchCts?.Cancel();
            searchCts?.Dispose();
            searchCts = new CancellationTokenSource();
            lastProgressUiUpdate = DateTimeOffset.MinValue;
            isSearchRunning = true;
            SetSearchUiState(isRunning: true);

            SetSummaryText("探索を開始します...");
            logger.Info($"Relink.Search start. rows={targetRows.Count}, folders={searchFolders.Count}");
            logger.Flush();

            var snapshots = BuildSearchInputRows(targetRows);

            var progress = new Progress<RelinkSearchProgressInfo>(p =>
            {
                var now = DateTimeOffset.Now;
                if (now - lastProgressUiUpdate < ProgressUiInterval && p.Done < p.Total)
                {
                    return;
                }

                lastProgressUiUpdate = now;
                var totalText = p.Total > 0 ? p.Total.ToString() : "-";
                SetSummaryText($"処理中: {p.CurrentFileName} ({p.Done}/{totalText})");
            });

            var execution = await searchService.ExecuteAsync(snapshots, searchFolders, searchCts.Token, progress);
            ApplyUpdates(targetRows, execution.Updates);
            SyncRows();

            if (IsRuntimeTimelineMode && suppressUiAfterClose && timelineContext is not null)
            {
                return;
            }

            var result = execution.Summary;
            SetSummaryText($"更新{result.UpdatedCount}件 / 曖昧{result.AmbiguousCount}件 / 未発見{result.NotFoundCount}件 / 失敗{result.FailedCount}件");
            logger.Info(
                $"Relink.Search end. scannedFilePathCount={result.ScannedFilePathCount}, missingCount={result.MissingCount}, updatedCount={result.UpdatedCount}, ambiguousCount={result.AmbiguousCount}, notFoundCount={result.NotFoundCount}, skippedCount={result.SkippedCount}, failedCount={result.FailedCount}");
            logger.Flush();

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage) && CanShowUiFeedback())
            {
                MessageBox.Show(result.ErrorMessage, "素材再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            logger.Info("Relink.Search canceled.");
            logger.Flush();
            SetSummaryText("探索をキャンセルしました。");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Relink.Search failed.");
            logger.Flush();
            if (CanShowUiFeedback())
            {
                MessageBox.Show("探索中にエラーが発生しました。ログを確認してください。", "素材再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            isSearchRunning = false;
            SetSearchUiState(isRunning: false);
            searchCts?.Dispose();
            searchCts = null;
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (saveCancellation is not null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        saveCancellation = cancellation;
        CancelSaveButton.IsEnabled = true;
        var token = cancellation.Token;
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
                var timelineRows = RelinkCandidateVerifier.SnapshotRows(timelineContext.Rows);
                var hashes = await RelinkCandidateVerifier.HashSelectedAsync(timelineRows, token);
                if (hashes.Count == 0) { SetSummaryText("更新対象はありません。"); return; }
                MessageBox.Show("期待ハッシュは読み込まれていないため、一致を判定できません。タイムラインへの反映は安全性を検証中のため停止しています。\n" + string.Join("\n\n", hashes), "素材再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetSummaryText("候補のハッシュを表示しました。タイムラインは変更していません。");
                return;
            }

            var snapshot = new RelinkDocumentContext { YmmpPath = context!.YmmpPath, OriginalJsonText = context.OriginalJsonText, OriginalBytes = context.OriginalBytes?.ToArray(), Rows = RelinkCandidateVerifier.SnapshotRows(context.Rows) };
            if (!snapshot.Rows.Any(row => row.Status == RelinkStatus.Updated && !string.IsNullOrWhiteSpace(row.SelectedCandidate)))
            { SetSummaryText("更新対象はありません。"); return; }
            AssetManifest? expectedManifest = null;
            var association = resolveMetadata is null ? null : await resolveMetadata(token);
            if (association is not null && metadataDirectory is not null)
            {
                await SaveManagedRelinkAsync(snapshot, association, token);
                return;
            }
            var fileHashes = await RelinkCandidateVerifier.HashSelectedAsync(snapshot.Rows, token);
            if (projectId is not null && MessageBox.Show("期待ハッシュを素材メタデータから照合しますか？", "素材再リンク", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                var metadataDialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json", Title = "素材メタデータを選択", CheckFileExists = true };
                if (metadataDialog.ShowDialog() != true) return;
                expectedManifest = await AssetManifestStore.LoadAsync(metadataDialog.FileName, token);
            }
            var comparison = expectedManifest is null ? fileHashes : await RelinkCandidateVerifier.CompareExpectedAsync(snapshot.Rows, snapshot.YmmpPath, projectId!.Value, expectedManifest, token);
            var notice = expectedManifest is null ? "期待ハッシュは未確認です。" : "素材メタデータは更新されません。不一致や期待値なしを確認してください。";
            if (MessageBox.Show(notice + "\n" + string.Join("\n\n", comparison) + "\n別名で保存しますか？", "素材再リンク", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var dialog = new SaveFileDialog { Filter = "YMM Project (*.ymmp)|*.ymmp", Title = "再リンク結果を新しいファイルに保存", OverwritePrompt = false };
            if (dialog.ShowDialog() != true) return;
            using var candidateLocks = new RelinkCandidateVerifier.CandidateReadLocks(snapshot.Rows);
            var checkedComparison = expectedManifest is null
                ? await RelinkCandidateVerifier.HashSelectedAsync(snapshot.Rows, token)
                : await RelinkCandidateVerifier.CompareExpectedAsync(snapshot.Rows, snapshot.YmmpPath, projectId!.Value, expectedManifest, token);
            if (!comparison.SequenceEqual(checkedComparison)) { SetSummaryText("候補が変更されました。再確認してください。"); return; }
            var (fileSuccess, fileErrorMessage) = await saveService.SaveAsAsync(snapshot, dialog.FileName, token);
            if (!fileSuccess)
            {
                logger.Info($"Relink.Save failed. mode=ymmp-file, ymmp={YmmpPath}, reason={fileErrorMessage}");
                logger.Flush();
                if (CanShowUiFeedback())
                {
                    MessageBox.Show(fileErrorMessage ?? "保存に失敗しました。ログを確認してください。", "素材再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }

            SetSummaryText($"別名で保存しました: {dialog.FileName}");
            logger.Info($"Relink.SaveAs end. mode=ymmp-file, ymmp={YmmpPath}");
            logger.Flush();
        }
        catch (OperationCanceledException) { SetSummaryText("再リンク処理を中止しました。"); }
        catch (Exception ex)
        {
            logger.Error(ex, $"Relink.Save failed. mode={(timelineContext is null ? "ymmp-file" : "runtime-timeline")}, ymmp={YmmpPath ?? "<none>"}");
            logger.Flush();
            if (CanShowUiFeedback())
            {
                MessageBox.Show("保存中にエラーが発生しました。ログを確認してください。", "素材再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally { saveCancellation = null; CancelSaveButton.IsEnabled = false; }
    }

    private async Task SaveManagedRelinkAsync(RelinkDocumentContext snapshot, AssetRevisionSeries.BranchSelection history, CancellationToken token)
    {
        if (!history.Graph.Heads.Contains(history.Selected.Receipt))
            throw new InvalidDataException("現在の分岐の先端を一覧で選び直してください。");
        var review = await AssetRelinkRecovery.ReviewAsync(snapshot, metadataDirectory!, history.Graph.Root, history.Selected.Receipt, history.Graph.Heads, token);
        var details = string.Join("\n\n", review.Select(item =>
            $"{item.OriginalPath}\n候補 {item.CandidatePath}\n期待 {item.ExpectedSha256 ?? "未登録"}\n実測 {item.CandidateSha256}"));
        var mismatches = review.Any(item => item.RequiresConfirmation);
        if (MessageBox.Show($"更新する分岐の先端 {history.Selected.Receipt.RevisionId}\n\n{details}\n\n{(mismatches ? "不一致または期待値のない候補を承認して続けますか？" : "候補は期待値と一致しました。続けますか？")}",
            "素材再リンク", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var dialog = new SaveFileDialog { Filter = "YMM Project (*.ymmp)|*.ymmp", OverwritePrompt = false,
            Title = "元のプロジェクトと同じフォルダーに別名で保存", InitialDirectory = Path.GetDirectoryName(snapshot.YmmpPath) };
        if (dialog.ShowDialog() != true) return;
        await using var sourceFile = ProjectFileEvidence.OpenRead(snapshot.YmmpPath);
        await VerifyManagedIdentityAsync(sourceFile, token);
        using var candidateLocks = new RelinkCandidateVerifier.CandidateReadLocks(snapshot.Rows);
        var evidence = await AssetRelinkRecovery.PrepareManagedOutputAsync(snapshot, dialog.FileName, metadataDirectory!, history.Graph.Root,
            history.Selected.Receipt, history.Graph.Heads, review, mismatches, token);
        try
        {
            var (saved, error) = await AssetRelinkRecovery.SavePreparedOutputAsync(evidence, snapshot, saveService, token);
            if (!saved)
            {
                SetSummaryText($"別名保存を確認できませんでした。{error}\n再リンクの回復記録\n{evidence}");
                return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetSummaryText($"再リンクを中止しました。回復記録から進行状況を確認できます。\n{evidence}");
            return;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            logger.Error(ex, "Managed relink publication failed after preparation.");
            SetSummaryText($"別名保存の状態を確認できませんでした。「再リンクの回復」で次の記録を確認してください。\n{evidence}");
            return;
        }
        AssetRelinkRecovery.OutputReceipt receipt;
        try
        {
            receipt = await AssetRelinkRecovery.CompleteForTransferAsync(evidence, metadataDirectory!, history.Graph.Root, token, snapshot.YmmpPath);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Managed relink metadata update failed after SaveAs.");
            SetSummaryText($"別名保存済みです。メタデータ更新は完了確認できませんでした。「再リンクの回復」で次の記録を選んでください。\n{evidence}");
            return;
        }
        await OfferAssociationTransferAsync(receipt, evidence, token);
    }

    private async void OnRecoverMetadataClick(object sender, RoutedEventArgs e)
    {
        if (saveCancellation is not null || resolveMetadata is null || metadataDirectory is null || YmmpPath is not { } source) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        saveCancellation = cancellation;
        CancelSaveButton.IsEnabled = true;
        try
        {
            var dialog = new OpenFileDialog { Filter = "素材再リンクの回復記録 (.ymmpm-relink-*.json)|.ymmpm-relink-*.json",
                InitialDirectory = Path.GetDirectoryName(source) };
            if (dialog.ShowDialog() != true) return;
            await using var sourceFile = ProjectFileEvidence.OpenRead(source);
            await VerifyManagedIdentityAsync(sourceFile, cancellation.Token);
            if (MessageBox.Show("選んだ記録から再リンクを回復しますか？\n出力がまだない場合は、検証済みの作業ファイルを当初の保存先に公開します。既存の出力は上書きしません。",
                "再リンクの回復", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var selection = await resolveMetadata(cancellation.Token);
            var receipt = await AssetRelinkRecovery.CompleteForTransferAsync(dialog.FileName, metadataDirectory, selection.Graph.Root,
                cancellation.Token, source, resumePreparedOutput: true);
            await OfferAssociationTransferAsync(receipt, dialog.FileName, cancellation.Token);
        }
        catch (OperationCanceledException) { SetSummaryText("再リンクの回復を中止しました。回復記録は保持されています。"); }
        catch (Exception ex)
        {
            logger.Error(ex, "Managed relink recovery failed.");
            SetSummaryText("回復できませんでした。元のプロジェクト、出力、候補とメタデータの現在版を確認してください。");
        }
        finally { saveCancellation = null; CancelSaveButton.IsEnabled = false; }
    }

    private void OnCancelSaveClick(object sender, RoutedEventArgs e) => saveCancellation?.Cancel();

    private async Task VerifyManagedIdentityAsync(FileStream source, CancellationToken token)
    {
        if (ExpectedSourceIdentity is { } expected && !expected.IsSameFile(await ProjectFileEvidence.ReadAsync(source, token)))
            throw new InvalidDataException("登録時と異なる元ファイルです。プロジェクトの識別情報を照合してください。");
    }

    private async Task OfferAssociationTransferAsync(AssetRelinkRecovery.OutputReceipt receipt, string evidence, CancellationToken token)
    {
        var completed = $"別名保存とメタデータ版を確認しました。\n{receipt.Output}\n版 {receipt.Revision.RevisionId}";
        if (TransferAssociationAsync is null || token.IsCancellationRequested ||
            MessageBox.Show($"一覧の登録先をこの出力へ切り替えますか？\n{receipt.Output}\n\nプロジェクト ID は引き継ぎ、元のファイルは保持します。",
                "再リンク出力への切り替え", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            SetSummaryText(completed + "\n登録先は現在の一覧のままです。");
            return;
        }
        try
        {
            await TransferAssociationAsync(receipt, token);
            SetSummaryText(completed + "\n一覧の登録先も切り替えました。");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Relink output saved; project-list association transfer did not complete.");
            SetSummaryText(completed + $"\n一覧の切り替えは完了確認できませんでした。一覧の保存状態と回復記録を確認してください。\n{evidence}");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnCancelSearchClick(object sender, RoutedEventArgs e)
    {
        CancelCurrentSearch();
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

    private static void ApplyUpdates(IEnumerable<RelinkRow> targetRows, IEnumerable<RelinkRowUpdate> updates)
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

    private static List<RelinkSearchInputRow> BuildSearchInputRows(IEnumerable<RelinkRow> targetRows)
    {
        return targetRows
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
    }

    private List<string> GetUsableSearchFolders()
    {
        YMMProjectManagerSettings.Current.Reload();

        var usable = new List<string>();
        foreach (var folder in YMMProjectManagerSettings.Current.GetSearchFolders())
        {
            if (!Directory.Exists(folder))
            {
                logger.Info($"Relink.Search skipped folder. not found: {folder}");
                continue;
            }

            if (!usable.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                usable.Add(folder);
            }
        }

        return usable;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (saveCancellation is { } saving)
        {
            saving.Cancel();
            e.Cancel = true;
            return;
        }
        if (!isSearchRunning)
        {
            return;
        }

        if (IsRuntimeTimelineMode)
        {
            var answer = MessageBox.Show(
                "探索中です。\n[はい] バックグラウンドで続行して閉じる\n[いいえ] キャンセルして閉じる\n[キャンセル] 閉じない",
                "素材再リンク",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (answer == MessageBoxResult.No)
            {
                CancelCurrentSearch();
            }

            suppressUiAfterClose = true;
            return;
        }

        CancelCurrentSearch();
        suppressUiAfterClose = true;
    }

    private void CancelCurrentSearch()
    {
        if (!isSearchRunning)
        {
            return;
        }

        try
        {
            searchCts?.Cancel();
        }
        catch
        {
        }
    }

    private void SetSearchUiState(bool isRunning)
    {
        if (suppressUiAfterClose || !IsLoaded)
        {
            return;
        }

        SearchButton.IsEnabled = !isRunning;
        CancelSearchButton.IsEnabled = isRunning;
    }

    private void SetSummaryText(string text)
    {
        if (suppressUiAfterClose || !IsLoaded)
        {
            return;
        }

        SummaryText.Text = text;
    }

    private bool CanShowUiFeedback()
    {
        return !suppressUiAfterClose && IsLoaded;
    }
}
