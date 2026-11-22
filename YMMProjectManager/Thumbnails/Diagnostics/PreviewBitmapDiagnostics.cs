using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using YMMProjectManager.Application.Diagnostics;

namespace YMMProjectManager.Infrastructure.Diagnostics;

/// <summary>
/// YMM のプレビュー表示からビットマップ取得可否を調べる診断サービスです。
/// </summary>
//CSJ,class=PreviewBitmapDiagnostics,プレビュービットマップ診断情報
public sealed class PreviewBitmapDiagnostics
{
    //CSJ,field=logger,ロガー
    private readonly FileLogger logger;

    public PreviewBitmapDiagnostics(FileLogger logger)
    {
        this.logger = logger;
    }

    //CSJ,method=RunAsync,実行する（非同期）
    public async Task<PreviewBitmapDiagnosticsResult> RunAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var result = new PreviewBitmapDiagnosticsResult
        {
            NextRecommendedCall = "GetBitmap(true)",
        };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            //CSJ,variable=dispatcher,ディスパッチャー
            var dispatcher = global::System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                // WPF ホスト外のテスト実行では UI オブジェクトへ触れず、安全な失敗結果を返す。
                result.FailureReason = "WPF dispatcher is unavailable.";
                return await FinalizeAsync(result, sw, cancellationToken).ConfigureAwait(false);
            }

            result.FailureReason = "Preview bitmap diagnostics are unavailable outside the host preview surface.";
            return await FinalizeAsync(result, sw, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Preview bitmap diagnostics failed.");
            result.FailureReason = ex.Message;
            return await FinalizeAsync(result, sw, cancellationToken).ConfigureAwait(false);
        }
    }

    //CSJ,method=FinalizeAsync,確定（非同期）
    private async Task<PreviewBitmapDiagnosticsResult> FinalizeAsync(
        PreviewBitmapDiagnosticsResult result,
        Stopwatch sw,
        //CSJ,parameter=cancellationToken,キャンセルトークン
        CancellationToken cancellationToken)
    {
        sw.Stop();
        result.Duration = sw.Elapsed;

        try
        {
            // 失敗結果も履歴として残し、環境依存の再現調査に使えるようにする。
            //CSJ,variable=outputDirectory,出力ディレクトリ
            var outputDirectory = Path.Combine(Path.GetTempPath(), "YMMProjectManager", "PreviewDiagnostics");
            Directory.CreateDirectory(outputDirectory);
            //CSJ,variable=path,パス
            var path = Path.Combine(outputDirectory, $"preview-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
            result.HistoryPath = path;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to write preview bitmap diagnostics history.");
        }

        return result;
    }
}
