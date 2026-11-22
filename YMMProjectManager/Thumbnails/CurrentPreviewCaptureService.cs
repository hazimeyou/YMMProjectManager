using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using YMMProjectManager.Application.Thumbnails;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Infrastructure.Output;

/// <summary>
/// 現在表示中の YMM プレビューを PNG と診断 JSON として保存します。
/// </summary>
//CSJ,class=CurrentPreviewCaptureService,現在のプレビュー画像取得サービス
public sealed class CurrentPreviewCaptureService
{
    //CSJ,field=logger,ロガー
    private readonly FileLogger logger;
    //CSJ,field=discoveryService,検出サービス
    private readonly YmmPreviewDiscoveryService? discoveryService;
    //CSJ,field=captureAdapter,キャプチャアダプター
    private readonly YmmPreviewBitmapCaptureAdapter? captureAdapter;
    private readonly IPreviewBitmapCaptureAdapter? injectedCaptureAdapter;
    //CSJ,field=outputDirectory,出力ディレクトリ
    private readonly string outputDirectory;
    private readonly Func<CancellationToken, Task<YmmPreviewDiscoveryResult>>? discoverAsync;

    public CurrentPreviewCaptureService(FileLogger logger)
    {
        this.logger = logger;
        discoveryService = new YmmPreviewDiscoveryService(logger);
        captureAdapter = new YmmPreviewBitmapCaptureAdapter(logger);
        outputDirectory = Path.Combine(Path.GetTempPath(), "YMMProjectManager", "current-preview-capture");
    }

    /// <summary>
    /// テストや診断用途で、探索処理と取得処理を差し替えられるコンストラクタです。
    /// </summary>
    public CurrentPreviewCaptureService(
        //CSJ,parameter=logger,ロガー
        FileLogger logger,
        //CSJ,parameter=captureAdapter,キャプチャアダプター
        IPreviewBitmapCaptureAdapter captureAdapter,
        //CSJ,parameter=outputDirectory,出力ディレクトリ
        string outputDirectory,
        Func<CancellationToken, Task<YmmPreviewDiscoveryResult>> discoverAsync)
    {
        this.logger = logger;
        injectedCaptureAdapter = captureAdapter;
        this.outputDirectory = outputDirectory;
        this.discoverAsync = discoverAsync;
    }

    //CSJ,method=CaptureAsync,取得する（非同期）
    public Task<CurrentPreviewCaptureResult> CaptureAsync(CancellationToken cancellationToken)
        => CaptureCurrentPreviewAsync(cancellationToken);

    //CSJ,method=CaptureCurrentPreviewAsync,現在のプレビューを取得する（非同期）
    public async Task<CurrentPreviewCaptureResult> CaptureCurrentPreviewAsync(CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        var sw = Stopwatch.StartNew();
        // キャプチャ画像と JSON を同じタイムスタンプにし、ログから対応を追いやすくする。
        Directory.CreateDirectory(outputDirectory);
        var stamp = started.ToString("yyyyMMdd-HHmmss");
        //CSJ,variable=pngPath,PNGパス
        var pngPath = Path.Combine(outputDirectory, $"current-preview-{stamp}.png");
        //CSJ,variable=jsonPath,JSONのパス
        var jsonPath = Path.Combine(outputDirectory, $"current-preview-{stamp}.json");

        var result = new CurrentPreviewCaptureResult
        {
            Timestamp = started,
            SavedPath = pngPath,
            DiagnosticsPath = jsonPath,
        };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 実行環境では WPF UI スレッドで探索し、テストでは注入された探索結果を使う。
            //CSJ,variable=discovery,検出
            var discovery = discoverAsync is not null
                ? await discoverAsync(cancellationToken).ConfigureAwait(true)
                : await InvokeOnUiAsync(discoveryService!.Discover).ConfigureAwait(true);
            CopyDiscoveryState(result, discovery);

            if (discovery.PreviewViewModel is null)
            {
                result.FailureReason = discovery.FailureReason ?? "PreviewViewModel not found.";
                return await FinalizeAsync(result, jsonPath, sw).ConfigureAwait(true);
            }

            // 通常は YMM の PreviewViewModel を直接叩き、テスト時は抽象化された取得アダプタを使う。
            //CSJ,variable=capture,キャプチャ
            var capture = captureAdapter is not null
                ? await InvokeOnUiAsync(() => captureAdapter.Capture(discovery.PreviewViewModel)).ConfigureAwait(true)
                : await CaptureWithInjectedAdapterAsync(cancellationToken).ConfigureAwait(true);

            result.InvocationSucceeded = capture.Success;
            result.CaptureSucceeded = capture.Success;
            result.GetBitmapMethodFound = capture.GetBitmapMethodFound;
            result.GetBitmapParameterTypes = capture.GetBitmapParameterTypes;
            result.NextRecommendedCall = capture.NextRecommendedCall;
            result.BitmapWidth = capture.BitmapWidth;
            result.BitmapHeight = capture.BitmapHeight;
            result.BitmapPixelFormat = capture.BitmapPixelFormat;

            if (!capture.Success || capture.Bitmap is null)
            {
                result.Success = false;
                result.FailureReason = capture.FailureReason;
                return await FinalizeAsync(result, jsonPath, sw).ConfigureAwait(true);
            }

            SaveBitmapSource(capture.Bitmap, pngPath);
            result.Success = true;
            result.CaptureSucceeded = true;
            result.FailureReason = null;
            result.SavedPath = pngPath;
            return await FinalizeAsync(result, jsonPath, sw).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.CaptureSucceeded = false;
            result.FailureReason = ex.Message;
            return await FinalizeAsync(result, jsonPath, sw).ConfigureAwait(true);
        }
    }

    //CSJ,method=CaptureCurrentPreviewBitmapAsync,現在のプレビュービットマップを取得する（非同期）
    public async Task<PreviewBitmapCaptureResult> CaptureCurrentPreviewBitmapAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        //CSJ,variable=discovery,検出
        var discovery = await InvokeOnUiAsync(discoveryService!.Discover).ConfigureAwait(true);
        if (discovery.PreviewViewModel is null)
        {
            return new PreviewBitmapCaptureResult
            {
                FailureReason = discovery.FailureReason ?? "PreviewViewModel not found.",
                GetBitmapMethodFound = discovery.GetBitmapMethodFound,
                GetBitmapParameterTypes = discovery.GetBitmapParameterTypes,
                NextRecommendedCall = discovery.NextRecommendedCall,
            };
        }

        return await InvokeOnUiAsync(() => captureAdapter!.Capture(discovery.PreviewViewModel)).ConfigureAwait(true);
    }

    /// <summary>
    /// IPreviewBitmapCaptureAdapter の結果を既存の出力 DTO に変換します。
    /// </summary>
    private async Task<PreviewBitmapCaptureResult> CaptureWithInjectedAdapterAsync(CancellationToken cancellationToken)
    {
        //CSJ,variable=capture,キャプチャ
        var capture = await injectedCaptureAdapter!.TryCaptureAsync(cancellationToken).ConfigureAwait(true);
        return new PreviewBitmapCaptureResult
        {
            Success = capture.Success,
            FailureReason = capture.FailureReason,
            GetBitmapMethodFound = capture.Success,
            NextRecommendedCall = "GetBitmap(true)",
            Bitmap = capture.Bitmap,
            BitmapWidth = capture.Bitmap?.PixelWidth ?? 0,
            BitmapHeight = capture.Bitmap?.PixelHeight ?? 0,
            BitmapPixelFormat = capture.Bitmap?.Format.ToString() ?? string.Empty,
        };
    }

    //CSJ,method=CopyDiscoveryState,検出状態をコピーする
    private static void CopyDiscoveryState(CurrentPreviewCaptureResult result, YmmPreviewDiscoveryResult discovery)
    {
        result.WindowCount = discovery.WindowCount;
        result.VisualTreeElementCount = discovery.VisualTreeElementCount;
        result.PreviewViewFound = discovery.PreviewViewFound;
        result.PreviewViewModelFound = discovery.PreviewViewModelFound;
        result.GetBitmapMethodFound = discovery.GetBitmapMethodFound;
        result.GetBitmapParameterTypes = discovery.GetBitmapParameterTypes;
        result.NextRecommendedCall = discovery.NextRecommendedCall;
    }

    //CSJ,method=FinalizeAsync,確定（非同期）
    private async Task<CurrentPreviewCaptureResult> FinalizeAsync(CurrentPreviewCaptureResult result, string jsonPath, Stopwatch sw)
    {
        sw.Stop();
        result.DurationMs = sw.Elapsed.TotalMilliseconds;
        result.DiagnosticsPath = jsonPath;

        try
        {
            // 成否に関係なく診断 JSON を残すことで、ユーザー環境での原因追跡を優先する。
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(jsonPath, json).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to write current preview diagnostics.");
        }

        logger.Info($"Current preview capture end. success={result.Success}, path={result.SavedPath}, reason={result.FailureReason ?? string.Empty}");
        logger.Flush();
        return result;
    }

    //CSJ,method=SaveBitmapSource,ビットマップ元を保存する
    private static void SaveBitmapSource(BitmapSource source, string pngPath)
    {
        //CSJ,variable=encoder,エンコーダー
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        //CSJ,variable=stream,ストリーム
        using var stream = File.Create(pngPath);
        encoder.Save(stream);
    }

    private static Task<T> InvokeOnUiAsync<T>(Func<T> action)
    {
        //CSJ,variable=dispatcher,ディスパッチャー
        var dispatcher = global::System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            // テストやホスト外実行では Dispatcher がないため、その場で安全に実行する。
            return Task.FromResult(action());
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}

/// <summary>
/// 現在プレビューのキャプチャ結果と診断 JSON の保存先です。
/// </summary>
//CSJ,class=CurrentPreviewCaptureResult,現在のプレビューキャプチャ結果
public sealed class CurrentPreviewCaptureResult
{
    //CSJ,property=Timestamp,タイムスタンプ
    public DateTimeOffset Timestamp { get; set; }
    //CSJ,property=Success,成功
    public bool Success { get; set; }
    //CSJ,property=FailureReason,失敗理由
    public string? FailureReason { get; set; }
    //CSJ,property=WindowCount,ウィンドウ件数
    public int WindowCount { get; set; }
    //CSJ,property=VisualTreeElementCount,表示ツリー要素件数
    public int VisualTreeElementCount { get; set; }
    //CSJ,property=PreviewViewFound,プレビュービューを検出したか
    public bool PreviewViewFound { get; set; }
    //CSJ,property=PreviewViewModelFound,プレビュービューモデルを検出したか
    public bool PreviewViewModelFound { get; set; }
    //CSJ,property=GetBitmapMethodFound,GetBitmapメソッドを検出したか
    public bool GetBitmapMethodFound { get; set; }
    //CSJ,property=GetBitmapParameterTypes,取得ビットマップ引数型一覧
    public string[] GetBitmapParameterTypes { get; set; } = [];
    //CSJ,property=NextRecommendedCall,次に推奨する呼び出し
    public string NextRecommendedCall { get; set; } = string.Empty;
    //CSJ,property=InvocationSucceeded,呼び出しが成功したか
    public bool InvocationSucceeded { get; set; }
    //CSJ,property=CaptureSucceeded,キャプチャ成功
    public bool CaptureSucceeded { get; set; }
    //CSJ,property=BitmapWidth,ビットマップ幅
    public int? BitmapWidth { get; set; }
    //CSJ,property=BitmapHeight,ビットマップ高さ
    public int? BitmapHeight { get; set; }
    //CSJ,property=BitmapPixelFormat,ビットマップ画素形式
    public string? BitmapPixelFormat { get; set; }
    //CSJ,property=SavedPath,保存済みのパス
    public string? SavedPath { get; set; }
    //CSJ,property=DiagnosticsPath,診断情報パス
    public string? DiagnosticsPath { get; set; }
    //CSJ,property=DurationMs,長さミリ秒
    public double DurationMs { get; set; }
}
