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
public sealed class CurrentPreviewCaptureService
{
    private readonly FileLogger logger;
    private readonly YmmPreviewDiscoveryService? discoveryService;
    private readonly YmmPreviewBitmapCaptureAdapter? captureAdapter;
    private readonly IPreviewBitmapCaptureAdapter? injectedCaptureAdapter;
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
        FileLogger logger,
        IPreviewBitmapCaptureAdapter captureAdapter,
        string outputDirectory,
        Func<CancellationToken, Task<YmmPreviewDiscoveryResult>> discoverAsync)
    {
        this.logger = logger;
        injectedCaptureAdapter = captureAdapter;
        this.outputDirectory = outputDirectory;
        this.discoverAsync = discoverAsync;
    }

    public Task<CurrentPreviewCaptureResult> CaptureAsync(CancellationToken cancellationToken)
        => CaptureCurrentPreviewAsync(cancellationToken);

    public async Task<CurrentPreviewCaptureResult> CaptureCurrentPreviewAsync(CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        var sw = Stopwatch.StartNew();
        // キャプチャ画像と JSON を同じタイムスタンプにし、ログから対応を追いやすくする。
        Directory.CreateDirectory(outputDirectory);
        var stamp = started.ToString("yyyyMMdd-HHmmss");
        var pngPath = Path.Combine(outputDirectory, $"current-preview-{stamp}.png");
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

    public async Task<PreviewBitmapCaptureResult> CaptureCurrentPreviewBitmapAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

    private static void SaveBitmapSource(BitmapSource source, string pngPath)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(pngPath);
        encoder.Save(stream);
    }

    private static Task<T> InvokeOnUiAsync<T>(Func<T> action)
    {
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
public sealed class CurrentPreviewCaptureResult
{
    public DateTimeOffset Timestamp { get; set; }
    public bool Success { get; set; }
    public string? FailureReason { get; set; }
    public int WindowCount { get; set; }
    public int VisualTreeElementCount { get; set; }
    public bool PreviewViewFound { get; set; }
    public bool PreviewViewModelFound { get; set; }
    public bool GetBitmapMethodFound { get; set; }
    public string[] GetBitmapParameterTypes { get; set; } = [];
    public string NextRecommendedCall { get; set; } = string.Empty;
    public bool InvocationSucceeded { get; set; }
    public bool CaptureSucceeded { get; set; }
    public int? BitmapWidth { get; set; }
    public int? BitmapHeight { get; set; }
    public string? BitmapPixelFormat { get; set; }
    public string? SavedPath { get; set; }
    public string? DiagnosticsPath { get; set; }
    public double DurationMs { get; set; }
}
