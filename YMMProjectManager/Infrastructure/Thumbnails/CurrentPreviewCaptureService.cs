using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using YMMProjectManager.Application.Thumbnails;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Infrastructure.Thumbnails;

public sealed class CurrentPreviewCaptureService : ICurrentPreviewCaptureService
{
    private readonly FileLogger logger;
    private readonly YmmPreviewDiscoveryService discoveryService;
    private readonly IPreviewBitmapCaptureAdapter captureAdapter;
    private readonly string outputDirectory;
    private readonly Func<CancellationToken, Task<YmmPreviewDiscoveryResult>> discoverAsync;

    public CurrentPreviewCaptureService(
        FileLogger logger,
        YmmPreviewDiscoveryService? discoveryService = null,
        IPreviewBitmapCaptureAdapter? captureAdapter = null,
        string? outputDirectory = null,
        Func<CancellationToken, Task<YmmPreviewDiscoveryResult>>? discoverAsync = null)
    {
        this.logger = logger;
        this.discoveryService = discoveryService ?? new YmmPreviewDiscoveryService(logger);
        this.captureAdapter = captureAdapter ?? new YmmPreviewBitmapCaptureAdapter(logger, this.discoveryService);
        this.outputDirectory = outputDirectory ?? Path.Combine(Path.GetTempPath(), "YMMProjectManager", "current-preview-capture");
        this.discoverAsync = discoverAsync ?? this.discoveryService.DiscoverAsync;
    }

    public async Task<CurrentPreviewCaptureResult> CaptureAsync(CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.Now;
        var fileStamp = timestamp.ToString("yyyyMMdd-HHmmss");
        var jsonPath = Path.Combine(outputDirectory, $"current-preview-{fileStamp}.json");
        var pngPath = Path.Combine(outputDirectory, $"current-preview-{fileStamp}.png");
        var watch = Stopwatch.StartNew();

        try
        {
            Directory.CreateDirectory(outputDirectory);
            logger.Info("Current preview capture start.");

            var discovery = await discoverAsync(cancellationToken).ConfigureAwait(true);
            if (captureAdapter is YmmPreviewBitmapCaptureAdapter concreteAdapter && discovery.DiscoverySucceeded)
            {
                concreteAdapter.CacheDiscovery(discovery);
            }

            var captureResult = await captureAdapter.TryCaptureAsync(cancellationToken).ConfigureAwait(true);
            var bitmap = captureResult.Bitmap;
            var saveSucceeded = false;
            string? failureReason = null;

            if (captureResult.Success && bitmap is not null)
            {
                try
                {
                    SavePng(bitmap, pngPath);
                    saveSucceeded = true;
                    logger.Info($"Current preview capture saved. path={pngPath}");
                }
                catch (Exception ex)
                {
                    failureReason = $"PNG save failed: {ex.Message}";
                    logger.Error(ex, "Current preview capture PNG save failed.");
                }
            }
            else
            {
                failureReason = captureResult.FailureReason ?? discovery.FailureReason ?? "Current preview capture failed";
            }

            watch.Stop();
            var result = new CurrentPreviewCaptureResult
            {
                Timestamp = timestamp,
                Success = captureResult.Success && saveSucceeded,
                FailureReason = failureReason,
                WindowCount = discovery.WindowCount,
                VisualTreeElementCount = discovery.VisualTreeElementCount,
                PreviewViewFound = discovery.PreviewViewFound,
                PreviewViewModelFound = discovery.PreviewViewModelFound,
                GetBitmapMethodFound = discovery.GetBitmapMethodFound,
                GetBitmapParameterTypes = discovery.GetBitmapParameterTypes,
                NextRecommendedCall = discovery.NextRecommendedCall,
                InvocationSucceeded = captureResult.Success,
                CaptureSucceeded = captureResult.Success && saveSucceeded,
                BitmapWidth = bitmap?.PixelWidth,
                BitmapHeight = bitmap?.PixelHeight,
                BitmapPixelFormat = bitmap?.Format.ToString(),
                SavedPath = saveSucceeded ? pngPath : null,
                DiagnosticsPath = jsonPath,
                DurationMs = watch.Elapsed.TotalMilliseconds,
            };

            WriteJson(jsonPath, result);
            logger.Info(
                $"Current preview capture end. success={result.Success}, invocation={result.InvocationSucceeded}, save={result.CaptureSucceeded}, width={result.BitmapWidth}, height={result.BitmapHeight}, pixelFormat={result.BitmapPixelFormat}, path={result.SavedPath}");
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            watch.Stop();
            logger.Error(ex, "Current preview capture failed.");

            var failed = new CurrentPreviewCaptureResult
            {
                Timestamp = timestamp,
                Success = false,
                FailureReason = ex.Message,
                WindowCount = 0,
                VisualTreeElementCount = 0,
                PreviewViewFound = false,
                PreviewViewModelFound = false,
                GetBitmapMethodFound = false,
                GetBitmapParameterTypes = [],
                NextRecommendedCall = null,
                InvocationSucceeded = false,
                CaptureSucceeded = false,
                BitmapWidth = null,
                BitmapHeight = null,
                BitmapPixelFormat = null,
                SavedPath = null,
                DiagnosticsPath = jsonPath,
                DurationMs = watch.Elapsed.TotalMilliseconds,
            };

            try
            {
                Directory.CreateDirectory(outputDirectory);
                WriteJson(jsonPath, failed);
            }
            catch
            {
            }

            return failed;
        }
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void WriteJson(string path, CurrentPreviewCaptureResult result)
    {
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        File.WriteAllText(path, json);
    }
}
