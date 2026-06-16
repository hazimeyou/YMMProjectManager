using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Infrastructure.Output;

public sealed class YmmPreviewDiscoveryService
{
    private readonly FileLogger logger;

    public YmmPreviewDiscoveryService(FileLogger logger)
    {
        this.logger = logger;
    }

    public YmmPreviewDiscoveryResult Discover()
    {
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
        foreach (Window window in app.Windows)
        {
            // Start from each top-level window, then walk both visual and logical links until PreviewViewModel is found.
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

        // Type-name matching keeps this resilient when the concrete view type moves across assemblies.
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
            // DataContext is the cheapest route to the preview view model when the visual tree already exposes the view.
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
            // Only Visual/Visual3D can participate in VisualTreeHelper traversal.
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

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }

    internal static bool CanUseVisualTree(DependencyObject dependencyObject)
    {
        // AvalonDock layout objects are not visuals, so they must be filtered out before VisualTreeHelper calls.
        return dependencyObject is Visual or Visual3D;
    }
}

public sealed class YmmPreviewDiscoveryResult
{
    public object? PreviewView { get; set; }
    public object? PreviewViewModel { get; set; }
    public int WindowCount { get; set; }
    public int VisualTreeElementCount { get; set; }
    public bool PreviewViewFound { get; set; }
    public bool PreviewViewModelFound { get; set; }
    public bool GetBitmapMethodFound { get; set; }
    public string[] GetBitmapParameterTypes { get; set; } = [];
    public string NextRecommendedCall { get; set; } = string.Empty;
    public string? FailureReason { get; set; }
    public MethodInfo? GetBitmapMethod { get; set; }
}
