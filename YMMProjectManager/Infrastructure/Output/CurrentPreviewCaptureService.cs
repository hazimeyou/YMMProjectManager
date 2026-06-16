using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Infrastructure.Output;

public sealed class CurrentPreviewCaptureService
{
    private readonly FileLogger logger;
    private readonly YmmPreviewDiscoveryService discoveryService;
    private readonly YmmPreviewBitmapCaptureAdapter captureAdapter;

    public CurrentPreviewCaptureService(FileLogger logger)
    {
        this.logger = logger;
        discoveryService = new YmmPreviewDiscoveryService(logger);
        captureAdapter = new YmmPreviewBitmapCaptureAdapter(logger);
    }

    public async Task<CurrentPreviewCaptureResult> CaptureCurrentPreviewAsync(CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        var sw = Stopwatch.StartNew();
        // Keep probe output isolated under %TEMP% so the investigation does not touch project state.
        var outputDirectory = Path.Combine(Path.GetTempPath(), "YMMProjectManager", "current-preview-capture");
        Directory.CreateDirectory(outputDirectory);
        var stamp = started.ToString("yyyyMMdd-HHmmss");
        var pngPath = Path.Combine(outputDirectory, $"current-preview-{stamp}.png");
        var jsonPath = Path.Combine(outputDirectory, $"current-preview-{stamp}.json");

        var result = new CurrentPreviewCaptureResult
        {
            Timestamp = started,
            SavedPath = pngPath,
        };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Discovery and bitmap capture both touch WPF objects, so they must run on the dispatcher thread.
            var discovery = await InvokeOnUiAsync(discoveryService.Discover).ConfigureAwait(true);
            CopyDiscoveryState(result, discovery);

            if (discovery.PreviewViewModel is null)
            {
                result.FailureReason = discovery.FailureReason ?? "PreviewViewModel not found.";
                return await FinalizeAsync(result, jsonPath, sw).ConfigureAwait(true);
            }

            var capture = await InvokeOnUiAsync(() => captureAdapter.Capture(discovery.PreviewViewModel)).ConfigureAwait(true);
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

    private static void CopyDiscoveryState(CurrentPreviewCaptureResult result, YmmPreviewDiscoveryResult discovery)
    {
        // Persist the discovery trace into the capture result so the JSON stands alone.
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
        result.DurationMs = sw.ElapsedMilliseconds;

        try
        {
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
        // Serialize the capture as a PNG so the preview can be checked visually without extra tooling.
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(pngPath);
        encoder.Save(stream);
    }

    private static Task<T> InvokeOnUiAsync<T>(Func<T> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return Task.FromResult(action());
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}

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
    public bool CaptureSucceeded { get; set; }
    public int BitmapWidth { get; set; }
    public int BitmapHeight { get; set; }
    public string? BitmapPixelFormat { get; set; }
    public string? SavedPath { get; set; }
    public long DurationMs { get; set; }
}
