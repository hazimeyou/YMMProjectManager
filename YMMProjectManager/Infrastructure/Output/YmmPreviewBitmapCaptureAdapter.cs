using System.Reflection;
using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMMProjectManager.Application.Thumbnails;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Infrastructure.Output;

public sealed class YmmPreviewBitmapCaptureAdapter : IPreviewBitmapCaptureAdapter
{
    private readonly FileLogger logger;

    public YmmPreviewBitmapCaptureAdapter()
        : this(new FileLogger(Path.Combine(Path.GetTempPath(), "YMMProjectManager", "logs", "preview-capture.log")))
    {
    }

    public YmmPreviewBitmapCaptureAdapter(FileLogger logger)
    {
        this.logger = logger;
    }

    public Task<YMMProjectManager.Application.Thumbnails.PreviewCaptureResult> TryCaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dispatcher = global::System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return Task.FromResult(YMMProjectManager.Application.Thumbnails.PreviewCaptureResult.Failed("WPF dispatcher is unavailable."));
        }

        return dispatcher.InvokeAsync(() =>
        {
            var discovery = new YmmPreviewDiscoveryService(logger).Discover();
            if (discovery.PreviewViewModel is null)
            {
                return YMMProjectManager.Application.Thumbnails.PreviewCaptureResult.Failed(discovery.FailureReason ?? "PreviewViewModel not found.");
            }

            var capture = Capture(discovery.PreviewViewModel);
            return capture.Success && capture.Bitmap is not null
                ? YMMProjectManager.Application.Thumbnails.PreviewCaptureResult.Succeeded(capture.Bitmap, capture.Bitmap.GetType().FullName ?? capture.Bitmap.GetType().Name)
                : YMMProjectManager.Application.Thumbnails.PreviewCaptureResult.Failed(capture.FailureReason ?? "Preview bitmap capture failed.");
        }).Task;
    }

    public PreviewBitmapCaptureResult Capture(object previewViewModel)
    {
        // まず最も有力な GetBitmap オーバーロードを優先し、失敗した場合だけ後続へ回す。
        if (!TrySelectGetBitmapMethod(previewViewModel, out var method, out var parameterTypes, out var nextRecommendedCall) || method is null)
        {
            return new PreviewBitmapCaptureResult
            {
                FailureReason = "GetBitmap method not found.",
                GetBitmapParameterTypes = parameterTypes,
                NextRecommendedCall = nextRecommendedCall,
            };
        }

        foreach (var attempt in CreateAttempts(method))
        {
            try
            {
                var value = attempt.Method.Invoke(previewViewModel, attempt.Args);
                if (value is null)
                {
                    continue;
                }

                if (!TryNormalizeBitmap(value, out var bitmap, out var width, out var height, out var pixelFormat))
                {
                    continue;
                }

                logger.Info($"Preview bitmap captured. type={value.GetType().FullName}, size={width}x{height}, pixelFormat={pixelFormat}");
                logger.Flush();
                return new PreviewBitmapCaptureResult
                {
                    Success = true,
                    Bitmap = bitmap,
                    BitmapWidth = width,
                    BitmapHeight = height,
                    BitmapPixelFormat = pixelFormat,
                    GetBitmapMethodFound = true,
                    GetBitmapParameterTypes = parameterTypes,
                    NextRecommendedCall = nextRecommendedCall,
                };
            }
            catch (TargetInvocationException ex)
            {
                logger.Info($"Preview bitmap attempt failed. method={attempt.Signature}, reason={ex.InnerException?.Message ?? ex.Message}");
            }
            catch (Exception ex)
            {
                logger.Info($"Preview bitmap attempt failed. method={attempt.Signature}, reason={ex.Message}");
            }
        }

        return new PreviewBitmapCaptureResult
        {
            FailureReason = "GetBitmap invocation failed.",
            GetBitmapMethodFound = true,
            GetBitmapParameterTypes = parameterTypes,
            NextRecommendedCall = nextRecommendedCall,
        };
    }

    internal static bool TrySelectGetBitmapMethod(
        object previewViewModel,
        out MethodInfo? method,
        out string[] parameterTypes,
        out string nextRecommendedCall)
    {
        method = null;
        parameterTypes = [];
        nextRecommendedCall = "GetBitmap(true)";

        var methods = previewViewModel.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => string.Equals(x.Name, "GetBitmap", StringComparison.Ordinal))
            .ToArray();

        if (methods.Length == 0)
        {
            return false;
        }

        // bool オーバーロードは実機プローブで成功済みのため、最優先で選ぶ。
        var ordered = methods
            .OrderByDescending(ScoreMethod)
            .ThenBy(x => x.GetParameters().Length)
            .ToArray();

        method = ordered[0];
        parameterTypes = method.GetParameters().Select(x => x.ParameterType.FullName ?? x.ParameterType.Name).ToArray();
        nextRecommendedCall = GetRecommendedCall(method);
        return true;
    }

    internal static bool TryNormalizeBitmap(object value, out BitmapSource bitmap, out int width, out int height, out string pixelFormat)
    {
        bitmap = null!;
        width = 0;
        height = 0;
        pixelFormat = string.Empty;

        // WPF と System.Drawing の両方を BitmapSource に揃え、呼び出し側で確実に PNG 化できるようにする。
        if (value is BitmapSource source)
        {
            bitmap = source;
            width = source.PixelWidth;
            height = source.PixelHeight;
            pixelFormat = source.Format.ToString();
            return true;
        }

        if (value.GetType().FullName != "System.Drawing.Bitmap")
        {
            return false;
        }

        return TryConvertDrawingBitmap(value, out bitmap, out width, out height, out pixelFormat);
    }

    private static IEnumerable<(MethodInfo Method, object?[] Args, string Signature)> CreateAttempts(MethodInfo method)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(bool))
        {
            yield return (method, [true], "GetBitmap(true)");
            yield return (method, [false], "GetBitmap(false)");
            yield break;
        }

        if (parameters.Length == 0)
        {
            yield return (method, [], "GetBitmap()");
        }
    }

    private static int ScoreMethod(MethodInfo method)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(bool))
        {
            return 3;
        }

        if (parameters.Length == 0)
        {
            return 2;
        }

        return 1;
    }

    private static string GetRecommendedCall(MethodInfo method)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(bool))
        {
            return "GetBitmap(true)";
        }

        if (parameters.Length == 0)
        {
            return "GetBitmap()";
        }

        return "GetBitmap(?)";
    }

    private static bool TryConvertDrawingBitmap(object bitmap, out BitmapSource source, out int width, out int height, out string pixelFormat)
    {
        source = null!;
        width = 0;
        height = 0;
        pixelFormat = string.Empty;

        var bitmapType = bitmap.GetType();
        var widthProperty = bitmapType.GetProperty("Width");
        var heightProperty = bitmapType.GetProperty("Height");
        var pixelFormatProperty = bitmapType.GetProperty("PixelFormat");
        if (widthProperty is null || heightProperty is null)
        {
            return false;
        }

        width = Convert.ToInt32(widthProperty.GetValue(bitmap));
        height = Convert.ToInt32(heightProperty.GetValue(bitmap));
        pixelFormat = pixelFormatProperty?.GetValue(bitmap)?.ToString() ?? string.Empty;

        var hBitmapMethod = bitmapType.GetMethod("GetHbitmap", Type.EmptyTypes);
        if (hBitmapMethod is null)
        {
            return false;
        }

        var hBitmap = hBitmapMethod.Invoke(bitmap, []);
        if (hBitmap is null)
        {
            return false;
        }

        try
        {
            var handle = hBitmap is IntPtr ptr ? ptr : new IntPtr(Convert.ToInt64(hBitmap));
            source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                handle,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            if (!source.IsFrozen && source.CanFreeze)
            {
                source.Freeze();
            }

            return true;
        }
        finally
        {
            if (hBitmap is IntPtr handle)
            {
                DeleteObject(handle);
            }
            else
            {
                DeleteObject(new IntPtr(Convert.ToInt64(hBitmap)));
            }
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}

public sealed class PreviewBitmapCaptureResult
{
    public bool Success { get; set; }
    public string? FailureReason { get; set; }
    public bool GetBitmapMethodFound { get; set; }
    public string[] GetBitmapParameterTypes { get; set; } = [];
    public string NextRecommendedCall { get; set; } = string.Empty;
    public BitmapSource? Bitmap { get; set; }
    public int BitmapWidth { get; set; }
    public int BitmapHeight { get; set; }
    public string BitmapPixelFormat { get; set; } = string.Empty;
}
