using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using YMMProjectManager.Infrastructure;
using YukkuriMovieMaker.Commons;

namespace YMMProjectManager.Infrastructure.Output;

public static class EditorInfoLocator
{
    public static IEditorInfo? TryResolve(FileLogger logger)
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app is null)
            {
                logger.Info("EditorInfoLocator: Application.Current is null.");
                return null;
            }

            foreach (Window window in app.Windows)
            {
                var editorInfo = TryResolveFromObject(window);
                if (editorInfo is not null)
                {
                    return editorInfo;
                }
            }

            if (app.MainWindow is not null)
            {
                var editorInfo = TryResolveFromObject(app.MainWindow);
                if (editorInfo is not null)
                {
                    return editorInfo;
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error("EditorInfoLocator failed.", ex);
        }

        return null;
    }

    private static IEditorInfo? TryResolveFromObject(object? root)
    {
        if (root is null)
        {
            return null;
        }

        var queue = new Queue<object>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current))
            {
                continue;
            }

            if (current is IEditorInfo info)
            {
                return info;
            }

            if (TryGetEditorInfoMember(current) is { } memberInfo)
            {
                return memberInfo;
            }

            if (current is IServiceProvider serviceProvider)
            {
                if (serviceProvider.GetService(typeof(IEditorInfo)) is IEditorInfo serviceEditorInfo)
                {
                    return serviceEditorInfo;
                }
            }

            if (current is FrameworkElement fe && fe.DataContext is not null)
            {
                queue.Enqueue(fe.DataContext);
            }

            if (current is FrameworkContentElement fce && fce.DataContext is not null)
            {
                queue.Enqueue(fce.DataContext);
            }

            if (current is DependencyObject dep)
            {
                var parent = VisualTreeHelper.GetParent(dep);
                if (parent is not null)
                {
                    queue.Enqueue(parent);
                }
            }

            EnqueueLikelyNestedObjects(current, queue);
        }

        return null;
    }

    private static IEditorInfo? TryGetEditorInfoMember(object current)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var type = current.GetType();

        foreach (var prop in type.GetProperties(flags))
        {
            if (prop.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (!typeof(IEditorInfo).IsAssignableFrom(prop.PropertyType))
            {
                continue;
            }

            if (prop.GetValue(current) is IEditorInfo info)
            {
                return info;
            }
        }

        foreach (var field in type.GetFields(flags))
        {
            if (!typeof(IEditorInfo).IsAssignableFrom(field.FieldType))
            {
                continue;
            }

            if (field.GetValue(current) is IEditorInfo info)
            {
                return info;
            }
        }

        return null;
    }

    private static void EnqueueLikelyNestedObjects(object current, Queue<object> queue)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var type = current.GetType();

        foreach (var prop in type.GetProperties(flags))
        {
            if (prop.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (prop.PropertyType == typeof(string))
            {
                continue;
            }

            if (prop.PropertyType.IsPrimitive || prop.PropertyType.IsEnum)
            {
                continue;
            }

            if (typeof(IEnumerable<object>).IsAssignableFrom(prop.PropertyType))
            {
                continue;
            }

            object? value;
            try
            {
                value = prop.GetValue(current);
            }
            catch
            {
                continue;
            }

            if (value is not null)
            {
                queue.Enqueue(value);
            }
        }
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
