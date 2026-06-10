using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using YMMProjectManager.Application.Diagnostics;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Infrastructure.Diagnostics;

public sealed class PreviewBitmapDiagnostics
{
    private readonly FileLogger logger;
    private readonly string diagnosticsDirectory;
    private readonly string resultJsonPath;
    private readonly string previewPngPath;

    public PreviewBitmapDiagnostics(FileLogger logger, string? diagnosticsDirectory = null)
    {
        this.logger = logger;
        this.diagnosticsDirectory = diagnosticsDirectory ?? Path.Combine(Path.GetTempPath(), "YMMProjectManager", "PreviewDiagnostics");
        resultJsonPath = Path.Combine(this.diagnosticsDirectory, "diagnostic-result.json");
        previewPngPath = Path.Combine(this.diagnosticsDirectory, "preview-test.png");
    }

    public async Task<PreviewBitmapDiagnosticsResult> RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dispatcher = global::System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            logger.Info("Preview Bitmap Diagnostics: dispatcher unavailable");
            var result = CreateFailureResult("dispatcher unavailable", TimeSpan.Zero);
            WriteResult(result);
            return result;
        }

        var watch = Stopwatch.StartNew();
        try
        {
            return await dispatcher.InvokeAsync(
                    () => RunOnUiThread(watch),
                    System.Windows.Threading.DispatcherPriority.Background,
                    cancellationToken)
                .Task
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Preview Bitmap Diagnostics failed.");
            var result = CreateFailureResult(ex.Message, watch.Elapsed);
            WriteResult(result);
            return result;
        }
    }

    private PreviewBitmapDiagnosticsResult RunOnUiThread(Stopwatch watch)
    {
        Directory.CreateDirectory(diagnosticsDirectory);

        var discovery = FindPreviewViewModel();
        if (discovery.Type is null)
        {
            logger.Info("PreviewViewModel not found");
            var result = CreateFailureResult("PreviewViewModel not found", watch.Elapsed);
            WriteResult(result);
            return result;
        }

        if (discovery.Instance is null)
        {
            logger.Info($"PreviewViewModel type found but instance unavailable. Type={discovery.Type.FullName}");
            var result = CreateFailureResult(
                "PreviewViewModel instance unavailable",
                watch.Elapsed,
                discovery.Type.FullName);
            WriteResult(result);
            return result;
        }

        logger.Info($"PreviewViewModel found. Type={discovery.Type.FullName}, Assembly={discovery.Type.Assembly.FullName}");

        var method = discovery.Type.GetMethod("GetBitmap", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (method is null)
        {
            logger.Info("GetBitmap not found");
            var result = CreateFailureResult(
                "GetBitmap not found",
                watch.Elapsed,
                discovery.Type.FullName,
                previewViewModelFound: true);
            WriteResult(result);
            return result;
        }

        var parameterCount = method.GetParameters().Length;
        var returnTypeName = method.ReturnType.FullName;
        logger.Info($"GetBitmap found. ReturnType={returnTypeName}, ParameterCount={parameterCount}");

        if (parameterCount != 0)
        {
            logger.Info("Capture failed");
            var result = CreateFailureResult(
                $"GetBitmap parameter count not supported: {parameterCount}",
                watch.Elapsed,
                discovery.Type.FullName,
                previewViewModelFound: true,
                getBitmapMethodFound: true,
                returnTypeName: returnTypeName);
            WriteResult(result);
            return result;
        }

        logger.Info("GetBitmap invoked");
        var captureWatch = Stopwatch.StartNew();
        object? bitmapObject;
        try
        {
            bitmapObject = method.Invoke(discovery.Instance, null);
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException ?? ex;
            logger.Error(inner, "Capture failed");
            var result = CreateFailureResult(
                $"GetBitmap threw exception: {inner.GetType().FullName}: {inner.Message}",
                watch.Elapsed,
                discovery.Type.FullName,
                previewViewModelFound: true,
                getBitmapMethodFound: true,
                returnTypeName: returnTypeName);
            WriteResult(result);
            return result;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Capture failed");
            var result = CreateFailureResult(
                $"GetBitmap invocation failed: {ex.GetType().FullName}: {ex.Message}",
                watch.Elapsed,
                discovery.Type.FullName,
                previewViewModelFound: true,
                getBitmapMethodFound: true,
                returnTypeName: returnTypeName);
            WriteResult(result);
            return result;
        }

        if (bitmapObject is null)
        {
            logger.Info("Capture failed");
            var result = CreateFailureResult(
                "GetBitmap returned null",
                watch.Elapsed,
                discovery.Type.FullName,
                previewViewModelFound: true,
                getBitmapMethodFound: true,
                returnTypeName: returnTypeName);
            WriteResult(result);
            return result;
        }

        var bitmapSource = ConvertToBitmapSource(bitmapObject, out var conversionWarning);
        if (bitmapSource is null)
        {
            logger.Info("Capture failed");
            var result = CreateFailureResult(
                $"Bitmap type unsupported: {bitmapObject.GetType().FullName}",
                watch.Elapsed,
                discovery.Type.FullName,
                previewViewModelFound: true,
                getBitmapMethodFound: true,
                returnTypeName: returnTypeName);
            WriteResult(result);
            return result;
        }

        if (!string.IsNullOrWhiteSpace(conversionWarning))
        {
            logger.Info($"Capture warning: {conversionWarning}");
        }

        try
        {
            SavePng(bitmapSource, previewPngPath);
            var fileSize = new FileInfo(previewPngPath).Length;
            logger.Info(
                $"Capture success. Width={bitmapSource.PixelWidth}, Height={bitmapSource.PixelHeight}, DurationMs={captureWatch.Elapsed.TotalMilliseconds:F1}");
            logger.Info($"Image saved. Path={previewPngPath}, FileSize={fileSize}");

            var result = new PreviewBitmapDiagnosticsResult
            {
                PreviewViewModelFound = true,
                GetBitmapMethodFound = true,
                CaptureSucceeded = true,
                PreviewViewModelTypeName = discovery.Type.FullName,
                GetBitmapReturnTypeName = returnTypeName,
                Width = bitmapSource.PixelWidth,
                Height = bitmapSource.PixelHeight,
                SavedFilePath = previewPngPath,
                Duration = watch.Elapsed,
            };

            WriteResult(result);
            return result;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Capture failed");
            var result = CreateFailureResult(
                $"PNG save failed: {ex.GetType().FullName}: {ex.Message}",
                watch.Elapsed,
                discovery.Type.FullName,
                previewViewModelFound: true,
                getBitmapMethodFound: true,
                returnTypeName: returnTypeName,
                width: bitmapSource.PixelWidth,
                height: bitmapSource.PixelHeight);
            WriteResult(result);
            return result;
        }
    }

    private PreviewBitmapDiagnosticsResult CreateFailureResult(
        string failureReason,
        TimeSpan duration,
        string? previewViewModelTypeName = null,
        bool previewViewModelFound = false,
        bool getBitmapMethodFound = false,
        string? returnTypeName = null,
        int? width = null,
        int? height = null)
        => new()
        {
            PreviewViewModelFound = previewViewModelFound,
            GetBitmapMethodFound = getBitmapMethodFound,
            CaptureSucceeded = false,
            PreviewViewModelTypeName = previewViewModelTypeName,
            GetBitmapReturnTypeName = returnTypeName,
            Width = width,
            Height = height,
            FailureReason = failureReason,
            Duration = duration,
        };

    private void WriteResult(PreviewBitmapDiagnosticsResult result)
    {
        try
        {
            Directory.CreateDirectory(diagnosticsDirectory);
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(resultJsonPath, json);
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Failed to save diagnostics result. Path={resultJsonPath}");
        }
    }

    private static PreviewViewModelDiscovery FindPreviewViewModel()
    {
        Type? discoveredType = null;
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

                discoveredType ??= type;
                var instance = TryGetStaticValue(type, "Current")
                    ?? TryGetStaticValue(type, "Instance")
                    ?? TryGetStaticValue(type, "Default");
                if (instance is not null)
                {
                    return new PreviewViewModelDiscovery(instance, type);
                }
            }
        }

        return new PreviewViewModelDiscovery(null, discoveredType);
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

    private static void SavePng(BitmapSource bitmapSource, string outputPath)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));

        using var stream = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private sealed record PreviewViewModelDiscovery(object? Instance, Type? Type);
}
