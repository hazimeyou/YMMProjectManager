using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;
using YMMProjectManager.Application.Thumbnails;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Infrastructure.Thumbnails;

public sealed class YmmPreviewBitmapCaptureAdapter : IPreviewBitmapCaptureAdapter
{
    private readonly YmmPreviewDiscoveryService discoveryService;
    private YmmPreviewDiscoveryResult? cachedDiscovery;

    public YmmPreviewBitmapCaptureAdapter(FileLogger? logger = null, YmmPreviewDiscoveryService? discoveryService = null)
    {
        this.discoveryService = discoveryService ?? new YmmPreviewDiscoveryService(logger ?? new FileLogger(Path.Combine(Path.GetTempPath(), "YMMProjectManager", "logs", "preview-capture.log")));
    }

    public void CacheDiscovery(YmmPreviewDiscoveryResult discovery)
    {
        if (discovery.DiscoverySucceeded)
        {
            cachedDiscovery = discovery;
        }
    }

    public async Task<PreviewCaptureResult> TryCaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var discovery = cachedDiscovery;
        if (discovery is null || !discovery.DiscoverySucceeded || discovery.TargetMethod is null || discovery.TargetInstance is null)
        {
            discovery = await discoveryService.DiscoverAsync(cancellationToken).ConfigureAwait(true);
            if (discovery.DiscoverySucceeded)
            {
                cachedDiscovery = discovery;
            }
        }

        if (!discovery.DiscoverySucceeded || discovery.TargetMethod is null || discovery.TargetInstance is null)
        {
            return PreviewCaptureResult.Failed(
                discovery.FailureReason ?? "PreviewViewModel not found",
                previewViewModelFound: discovery.PreviewViewModelFound,
                getBitmapFound: discovery.GetBitmapMethodFound,
                returnType: discovery.GetBitmapReturnTypeName,
                warnings: BuildWarnings(discovery));
        }

        try
        {
            return await InvokeOnUiThreadAsync(discovery, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PreviewCaptureResult.Failed(
                ex.Message,
                previewViewModelFound: discovery.PreviewViewModelFound,
                getBitmapFound: discovery.GetBitmapMethodFound,
                returnType: discovery.GetBitmapReturnTypeName,
                warnings: BuildWarnings(discovery));
        }
    }

    private async Task<PreviewCaptureResult> InvokeOnUiThreadAsync(YmmPreviewDiscoveryResult discovery, CancellationToken cancellationToken)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return PreviewCaptureResult.Failed("dispatcher unavailable", previewViewModelFound: discovery.PreviewViewModelFound, getBitmapFound: discovery.GetBitmapMethodFound, returnType: discovery.GetBitmapReturnTypeName);
        }

        return await dispatcher.InvokeAsync(() => CaptureOnUiThread(discovery), System.Windows.Threading.DispatcherPriority.Background, cancellationToken).Task.ConfigureAwait(true);
    }

    private static PreviewCaptureResult CaptureOnUiThread(YmmPreviewDiscoveryResult discovery)
    {
        var bitmap = discovery.TargetMethod!.Invoke(discovery.TargetInstance, discovery.TargetArguments);
        if (bitmap is null)
        {
            return PreviewCaptureResult.Failed("GetBitmap returned null", previewViewModelFound: true, getBitmapFound: true, returnType: discovery.TargetMethod.ReturnType.FullName, warnings: BuildWarnings(discovery));
        }

        var converted = ConvertToBitmapSource(bitmap, out var conversionWarning);
        if (converted is null)
        {
            return PreviewCaptureResult.Failed(
                $"Unsupported bitmap type: {bitmap.GetType().FullName}",
                previewViewModelFound: true,
                getBitmapFound: true,
                returnType: discovery.TargetMethod.ReturnType.FullName,
                warnings: BuildWarnings(discovery, conversionWarning));
        }

        var warnings = BuildWarnings(discovery, conversionWarning);
        return PreviewCaptureResult.Succeeded(converted, discovery.PreviewViewModelTypeName ?? discovery.TargetType?.FullName ?? "PreviewViewModel", warnings);
    }

    private static BitmapSource? ConvertToBitmapSource(object bitmap, out string? warning)
    {
        warning = null;
        if (bitmap is BitmapSource bitmapSource)
        {
            var clone = bitmapSource.CloneCurrentValue();
            if (!clone.IsFrozen && clone.CanFreeze)
            {
                clone.Freeze();
            }

            return clone;
        }

        if (bitmap is System.Drawing.Bitmap drawingBitmap)
        {
            warning = "GetBitmap returned System.Drawing.Bitmap.";
            using var memory = new MemoryStream();
            drawingBitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Bmp);
            memory.Position = 0;
            var decoder = BitmapDecoder.Create(memory, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var source = BitmapFrame.Create(frame);
            if (!source.IsFrozen && source.CanFreeze)
            {
                source.Freeze();
            }

            return source;
        }

        return null;
    }

    private static IReadOnlyList<string> BuildWarnings(YmmPreviewDiscoveryResult discovery, string? extraWarning = null)
    {
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(extraWarning))
        {
            warnings.Add(extraWarning!);
        }

        if (!string.IsNullOrWhiteSpace(discovery.GetBitmapSignatureCategory))
        {
            warnings.Add($"Discovery category={discovery.GetBitmapSignatureCategory}");
        }

        return warnings;
    }
}

public sealed record PreviewCaptureResult(
    bool Success,
    BitmapSource? Bitmap,
    string? FailureReason,
    bool PreviewViewModelFound,
    bool GetBitmapFound,
    string? ReturnType,
    IReadOnlyList<string> Warnings)
{
    public static PreviewCaptureResult Succeeded(BitmapSource bitmap, string returnType, IReadOnlyList<string>? warnings = null)
        => new(true, bitmap, null, true, true, returnType, warnings ?? []);

    public static PreviewCaptureResult Failed(
        string reason,
        bool previewViewModelFound = false,
        bool getBitmapFound = false,
        string? returnType = null,
        IReadOnlyList<string>? warnings = null)
        => new(false, null, reason, previewViewModelFound, getBitmapFound, returnType, warnings ?? []);
}
