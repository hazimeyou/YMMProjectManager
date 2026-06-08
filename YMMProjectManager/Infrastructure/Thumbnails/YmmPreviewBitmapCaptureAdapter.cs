using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace YMMProjectManager.Infrastructure.Thumbnails;

public sealed class YmmPreviewBitmapCaptureAdapter
{
    public async Task<PreviewCaptureResult> TryCaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return PreviewCaptureResult.Failed("dispatcher unavailable");
        }

        try
        {
            return await dispatcher.InvokeAsync(CaptureOnUiThread, System.Windows.Threading.DispatcherPriority.Background, cancellationToken).Task.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PreviewCaptureResult.Failed(ex.Message);
        }
    }

    private static PreviewCaptureResult CaptureOnUiThread()
    {
        var previewViewModel = FindPreviewViewModel(out var viewModelType);
        if (previewViewModel is null || viewModelType is null)
        {
            return PreviewCaptureResult.Failed("PreviewViewModel not found");
        }

        var method = viewModelType.GetMethod("GetBitmap", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (method is null)
        {
            return PreviewCaptureResult.Failed("GetBitmap not found", previewViewModelFound: true, getBitmapFound: false);
        }

        var bitmap = method.Invoke(previewViewModel, null);
        if (bitmap is null)
        {
            return PreviewCaptureResult.Failed("GetBitmap returned null", previewViewModelFound: true, getBitmapFound: true);
        }

        var converted = ConvertToBitmapSource(bitmap, out var conversionWarning);
        if (converted is null)
        {
            return PreviewCaptureResult.Failed(
                $"Unsupported bitmap type: {bitmap.GetType().FullName}",
                previewViewModelFound: true,
                getBitmapFound: true,
                returnType: bitmap.GetType().FullName,
                warnings: string.IsNullOrWhiteSpace(conversionWarning) ? [] : [conversionWarning!]);
        }

        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(conversionWarning))
        {
            warnings.Add(conversionWarning);
        }

        return PreviewCaptureResult.Succeeded(converted, previewViewModel.GetType().FullName ?? viewModelType.FullName ?? "PreviewViewModel", warnings);
    }

    private static object? FindPreviewViewModel(out Type? previewViewModelType)
    {
        previewViewModelType = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(type => type is not null).Cast<Type>().ToArray();
            }

            foreach (var type in types)
            {
                if (!string.Equals(type.Name, "PreviewViewModel", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var instance = TryGetStaticValue(type, "Current")
                    ?? TryGetStaticValue(type, "Instance")
                    ?? TryGetStaticValue(type, "Default");
                if (instance is not null)
                {
                    previewViewModelType = type;
                    return instance;
                }
            }
        }

        return null;
    }

    private static object? TryGetStaticValue(Type type, string memberName)
    {
        var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (property is not null)
        {
            return property.GetValue(null);
        }

        var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return field?.GetValue(null);
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

        var bitmapType = bitmap.GetType();
        if (string.Equals(bitmapType.FullName, "System.Drawing.Bitmap", StringComparison.Ordinal))
        {
            return ConvertDrawingBitmapToBitmapSource(bitmap);
        }

        var getHbitmap = bitmapType.GetMethod("GetHbitmap", BindingFlags.Public | BindingFlags.Instance);
        if (getHbitmap is not null && getHbitmap.ReturnType == typeof(IntPtr))
        {
            warning = "GetBitmap returned HBITMAP-compatible object.";
            return ConvertDrawingBitmapToBitmapSource(bitmap);
        }

        return null;
    }

    private static BitmapSource ConvertDrawingBitmapToBitmapSource(object bitmap)
    {
        var getHbitmap = bitmap.GetType().GetMethod("GetHbitmap", BindingFlags.Public | BindingFlags.Instance);
        if (getHbitmap is null)
        {
            throw new InvalidOperationException("GetHbitmap not found.");
        }

        var hBitmap = (IntPtr)getHbitmap.Invoke(bitmap, null)!;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            if (!source.IsFrozen && source.CanFreeze)
            {
                source.Freeze();
            }

            return source;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
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
