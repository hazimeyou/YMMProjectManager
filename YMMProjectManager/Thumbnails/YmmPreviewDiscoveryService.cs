using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Infrastructure.Output;

/// <summary>
/// 開いている WPF ウィンドウから YMM の PreviewViewModel を探索します。
/// </summary>
//CSJ,class=YmmPreviewDiscoveryService,YMMプレビュー検出サービス
public sealed class YmmPreviewDiscoveryService
{
    //CSJ,field=logger,ロガー
    private readonly FileLogger logger;

    public YmmPreviewDiscoveryService(FileLogger logger)
    {
        this.logger = logger;
    }

    //CSJ,method=Discover,検出する
    public YmmPreviewDiscoveryResult Discover()
    {
        //CSJ,variable=app,アプリケーション
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return new YmmPreviewDiscoveryResult
            {
                FailureReason = "Application.Current is null.",
            };
        }

        var result = new YmmPreviewDiscoveryResult
        {
            WindowCount = app.Windows.Count,
        };

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        //CSJ,variable=window,ウィンドウ
        foreach (Window window in app.Windows)
        {
            // 各トップレベルウィンドウから、VisualTree と DataContext の両方をたどる。
            Traverse(window, visited, result);
            if (result.PreviewViewModel is not null)
            {
                break;
            }
        }

        if (result.PreviewViewModel is not null)
        {
            result.GetBitmapMethodFound = YmmPreviewBitmapCaptureAdapter.TrySelectGetBitmapMethod(
                result.PreviewViewModel,
                out var method,
                out var parameterTypes,
                out var nextRecommendedCall);
            result.GetBitmapMethod = method;
            result.GetBitmapParameterTypes = parameterTypes;
            result.NextRecommendedCall = nextRecommendedCall;
        }

        if (!result.PreviewViewModelFound)
        {
            result.FailureReason = "PreviewViewModel not found.";
        }
        else if (!result.GetBitmapMethodFound)
        {
            result.FailureReason = "GetBitmap method not found.";
        }

        logger.Info(
            $"Preview discovery. windows={result.WindowCount}, elements={result.VisualTreeElementCount}, viewFound={result.PreviewViewFound}, vmFound={result.PreviewViewModelFound}, getBitmap={result.GetBitmapMethodFound}");
        logger.Flush();
        return result;
    }

    private static void Traverse(object current, HashSet<object> visited, YmmPreviewDiscoveryResult result)
    {
        if (!visited.Add(current))
        {
            return;
        }

        result.VisualTreeElementCount++;

        // 具体型が別アセンブリへ移動しても追えるよう、型名ベースで候補を判定する。
        //CSJ,variable=typeName,型名前
        var typeName = current.GetType().Name;
        if (!result.PreviewViewFound && typeName.Contains("PreviewView", StringComparison.OrdinalIgnoreCase))
        {
            result.PreviewView = current;
            result.PreviewViewFound = true;
        }

        if (!result.PreviewViewModelFound && typeName.Contains("PreviewViewModel", StringComparison.OrdinalIgnoreCase))
        {
            result.PreviewViewModel = current;
            result.PreviewViewModelFound = true;
        }

        if (current is FrameworkElement fe && fe.DataContext is not null)
        {
            // 画面要素より DataContext の方が PreviewViewModel に近いことが多い。
            if (!result.PreviewViewModelFound && fe.DataContext.GetType().Name.Contains("PreviewViewModel", StringComparison.OrdinalIgnoreCase))
            {
                result.PreviewViewModel = fe.DataContext;
                result.PreviewViewModelFound = true;
            }

            Traverse(fe.DataContext, visited, result);
        }

        if (current is FrameworkContentElement fce && fce.DataContext is not null)
        {
            if (!result.PreviewViewModelFound && fce.DataContext.GetType().Name.Contains("PreviewViewModel", StringComparison.OrdinalIgnoreCase))
            {
                result.PreviewViewModel = fce.DataContext;
                result.PreviewViewModelFound = true;
            }

            Traverse(fce.DataContext, visited, result);
        }

        if (current is DependencyObject dep && CanUseVisualTree(dep))
        {
            // VisualTreeHelper が扱える型だけを対象にして、AvalonDock 系の例外を避ける。
            //CSJ,variable=childCount,子件数
            var childCount = VisualTreeHelper.GetChildrenCount(dep);
            for (var i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(dep, i);
                if (child is not null)
                {
                    Traverse(child, visited, result);
                }
            }
        }

        var contentProp = current.GetType().GetProperty("Content", BindingFlags.Public | BindingFlags.Instance);
        if (contentProp is not null && contentProp.PropertyType != typeof(string))
        {
            object? value;
            try
            {
                value = contentProp.GetValue(current);
            }
            catch
            {
                value = null;
            }

            if (value is not null)
            {
                Traverse(value, visited, result);
            }
        }
    }

    //CSJ,class=ReferenceEqualityComparer,参照等価比較比較器
    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        //CSJ,field=Instance,インスタンス
        public static readonly ReferenceEqualityComparer Instance = new();

        //CSJ,method=Equals,等しいか判定する
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        //CSJ,method=GetHashCode,ハッシュコードを取得する
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }

    internal static bool CanUseVisualTree(DependencyObject dependencyObject)
    {
        // VisualTreeHelper 呼び出し前に除外し、ホスト UI 固有のツリー構造で落ちないようにする。
        return dependencyObject is Visual or Visual3D;
    }
}

/// <summary>
/// プレビュー探索で見つかった View/ViewModel と GetBitmap 候補の情報です。
/// </summary>
//CSJ,class=YmmPreviewDiscoveryResult,YMMプレビュー検出結果
public sealed class YmmPreviewDiscoveryResult
{
    //CSJ,property=DiscoverySucceeded,検出成功
    public bool DiscoverySucceeded { get; set; }
    //CSJ,property=FailureStage,失敗した段階
    public string? FailureStage { get; set; }
    //CSJ,property=PreviewView,プレビュービュー
    public object? PreviewView { get; set; }
    //CSJ,property=PreviewViewModel,プレビュービューモデル
    public object? PreviewViewModel { get; set; }
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
    //CSJ,property=FailureReason,失敗理由
    public string? FailureReason { get; set; }
    //CSJ,property=GetBitmapMethod,取得ビットマップメソッド
    public MethodInfo? GetBitmapMethod { get; set; }
}
