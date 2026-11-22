using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMMProjectManager.Application.Thumbnails;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Infrastructure.Output;

/// <summary>
/// YMM の PreviewViewModel から GetBitmap を呼び出し、WPF で保存可能なビットマップへ変換します。
/// </summary>
//CSJ,class=YmmPreviewBitmapCaptureAdapter,YMMプレビュービットマップキャプチャアダプター
public sealed class YmmPreviewBitmapCaptureAdapter : IPreviewBitmapCaptureAdapter
{
    //CSJ,field=logger,ロガー
    private readonly FileLogger logger;

    public YmmPreviewBitmapCaptureAdapter()
        : this(new FileLogger(Path.Combine(Path.GetTempPath(), "YMMProjectManager", "logs", "preview-capture.log")))
    {
    }

    public YmmPreviewBitmapCaptureAdapter(FileLogger logger)
    {
        this.logger = logger;
    }

    //CSJ,method=TryCaptureAsync,キャプチャを試みる（非同期）
    public Task<YMMProjectManager.Application.Thumbnails.PreviewCaptureResult> TryCaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        //CSJ,variable=dispatcher,ディスパッチャー
        var dispatcher = global::System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            // ホスト外では UI オブジェクトに触れず、呼び出し側で扱える失敗結果にする。
            return Task.FromResult(YMMProjectManager.Application.Thumbnails.PreviewCaptureResult.Failed("WPF dispatcher is unavailable."));
        }

        return dispatcher.InvokeAsync(() =>
        {
            //CSJ,variable=discovery,検出
            var discovery = new YmmPreviewDiscoveryService(logger).Discover();
            if (discovery.PreviewViewModel is null)
            {
                return YMMProjectManager.Application.Thumbnails.PreviewCaptureResult.Failed(discovery.FailureReason ?? "PreviewViewModel not found.");
            }

            //CSJ,variable=capture,キャプチャ
            var capture = Capture(discovery.PreviewViewModel);
            return capture.Success && capture.Bitmap is not null
                ? YMMProjectManager.Application.Thumbnails.PreviewCaptureResult.Succeeded(capture.Bitmap, capture.Bitmap.GetType().FullName ?? capture.Bitmap.GetType().Name)
                : YMMProjectManager.Application.Thumbnails.PreviewCaptureResult.Failed(capture.FailureReason ?? "Preview bitmap capture failed.");
        }).Task;
    }

    //CSJ,method=Capture,取得する
    public PreviewBitmapCaptureResult Capture(object previewViewModel)
    {
        // まず最も成功しやすい GetBitmap オーバーロードを選び、失敗時は後続候補へ回す。
        if (!TrySelectGetBitmapMethod(previewViewModel, out var method, out var parameterTypes, out var nextRecommendedCall) || method is null)
        {
            return new PreviewBitmapCaptureResult
            {
                FailureReason = "GetBitmap method not found.",
                GetBitmapParameterTypes = parameterTypes,
                NextRecommendedCall = nextRecommendedCall,
            };
        }

        //CSJ,variable=attempt,試行
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

    //CSJ,method=TrySelectGetBitmapMethod,取得ビットマップメソッドの選択するを試みる
    internal static bool TrySelectGetBitmapMethod(
        //CSJ,parameter=previewViewModel,プレビュービューモデル
        object previewViewModel,
        //CSJ,parameter=method,メソッド
        out MethodInfo? method,
        //CSJ,parameter=parameterTypes,引数型一覧
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

        // 実機で成功例の多い bool オーバーロードを優先し、次に引数なしを試す。
        var ordered = methods
            .OrderByDescending(ScoreMethod)
            .ThenBy(x => x.GetParameters().Length)
            .ToArray();

        method = ordered[0];
        parameterTypes = method.GetParameters().Select(x => x.ParameterType.FullName ?? x.ParameterType.Name).ToArray();
        nextRecommendedCall = GetRecommendedCall(method);
        return true;
    }

    //CSJ,method=TryNormalizeBitmap,ビットマップの正規化を試みる
    internal static bool TryNormalizeBitmap(object value, out BitmapSource bitmap, out int width, out int height, out string pixelFormat)
    {
        bitmap = null!;
        width = 0;
        height = 0;
        pixelFormat = string.Empty;

        // WPF と System.Drawing のどちらが返っても、保存処理では BitmapSource に揃える。
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

    //CSJ,method=CreateAttempts,試行回数を作成する
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

    //CSJ,method=ScoreMethod,得点メソッド
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

        //CSJ,variable=bitmapType,ビットマップ型
        var bitmapType = bitmap.GetType();
        //CSJ,variable=widthProperty,幅プロパティ
        var widthProperty = bitmapType.GetProperty("Width");
        //CSJ,variable=heightProperty,高さプロパティ
        var heightProperty = bitmapType.GetProperty("Height");
        //CSJ,variable=pixelFormatProperty,画素形式プロパティ
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
            // System.Drawing.Bitmap は GDI ハンドル経由で WPF BitmapSource へ変換する。
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
            // GetHbitmap で取得した GDI オブジェクトは必ず解放する。
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

    //CSJ,method=DeleteObject,オブジェクトを削除する
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}

/// <summary>
/// PreviewViewModel からのビットマップ取得結果です。
/// </summary>
//CSJ,class=PreviewBitmapCaptureResult,プレビュービットマップキャプチャ結果
public sealed class PreviewBitmapCaptureResult
{
    //CSJ,property=Success,成功
    public bool Success { get; set; }
    //CSJ,property=FailureReason,失敗理由
    public string? FailureReason { get; set; }
    //CSJ,property=GetBitmapMethodFound,GetBitmapメソッドを検出したか
    public bool GetBitmapMethodFound { get; set; }
    //CSJ,property=GetBitmapParameterTypes,取得ビットマップ引数型一覧
    public string[] GetBitmapParameterTypes { get; set; } = [];
    //CSJ,property=NextRecommendedCall,次に推奨する呼び出し
    public string NextRecommendedCall { get; set; } = string.Empty;
    //CSJ,property=Bitmap,ビットマップ
    public BitmapSource? Bitmap { get; set; }
    //CSJ,property=BitmapWidth,ビットマップ幅
    public int BitmapWidth { get; set; }
    //CSJ,property=BitmapHeight,ビットマップ高さ
    public int BitmapHeight { get; set; }
    //CSJ,property=BitmapPixelFormat,ビットマップ画素形式
    public string BitmapPixelFormat { get; set; } = string.Empty;
}
