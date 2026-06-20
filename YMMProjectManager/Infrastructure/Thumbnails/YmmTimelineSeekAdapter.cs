using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using YMMProjectManager.Application.Thumbnails;

namespace YMMProjectManager.Infrastructure.Thumbnails;

/// <summary>
/// YMM のタイムラインらしきオブジェクトを探索し、指定フレームへ移動します。
/// </summary>
public sealed class YmmTimelineSeekAdapter : ITimelineSeekAdapter
{
    private const int FrameTolerance = 1;
    private const int MaxDiscoveryDepth = 6;
    private const int MaxDiscoveryNodes = 2048;

    private static readonly string[] CommandNameCandidates = ["ScrollToFrame", "シーク", "Seek"];
    private static readonly string[] PriorityPropertyNames =
    [
        "Timeline",
        "TimelineToolInfo",
        "Info",
        "DataContext",
        "Content",
        "ViewModel",
        "ProjectListViewModel",
    ];

    public async Task<SeekResult> SeekAsync(object? timeline, int targetFrame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // YMM の UI オブジェクトを触るため、シーク処理は必ず WPF Dispatcher 上で実行する。
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return SeekResult.Failed(
                targetFrame,
                "dispatcher unavailable",
                methodUsed: "Failed",
                tolerance: FrameTolerance);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await dispatcher.InvokeAsync(
                () => SeekOnUiThread(timeline, targetFrame),
                DispatcherPriority.Send,
                cancellationToken).Task.ConfigureAwait(true);

            sw.Stop();
            return result with { Duration = sw.Elapsed };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return SeekResult.Failed(
                targetFrame,
                ex.Message,
                methodUsed: "Failed",
                exceptionType: ex.GetType().FullName,
                duration: sw.Elapsed,
                tolerance: FrameTolerance);
        }
    }

    public async Task<string> WriteSeekProbeAsync(object? timeline, int targetFrame, string outputDirectory, CancellationToken cancellationToken)
    {
        var result = await SeekAsync(timeline, targetFrame, cancellationToken).ConfigureAwait(true);
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, $"seek-probe-{DateTime.Now:yyyyMMdd-HHmmss-fff}-frame-{targetFrame:D6}.json");
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static SeekResult SeekOnUiThread(object? timeline, int targetFrame)
    {
        var discovery = DiscoverTimeline(timeline);
        if (discovery.Timeline is null)
        {
            return SeekResult.Failed(
                targetFrame,
                discovery.FailureReason ?? "timeline not found",
                methodUsed: "Failed",
                tolerance: FrameTolerance);
        }

        // まず最も単純で副作用が少ない CurrentFrame プロパティ経由を試す。
        var directResult = TrySeekViaCurrentFrame(discovery.Timeline, targetFrame);
        if (directResult.Success)
        {
            return directResult;
        }

        // CurrentFrame が読み取り専用の場合は、YMM のコマンドバインディング経由で移動を試す。
        var fallbackResult = TrySeekViaCommandBindings(discovery, targetFrame);
        if (fallbackResult.Success)
        {
            return fallbackResult;
        }

        var combinedReason = CombineReasons(directResult.FailureReason, fallbackResult.FailureReason, discovery.FailureReason);
        return fallbackResult with
        {
            FailureReason = combinedReason,
            ExceptionType = fallbackResult.ExceptionType ?? directResult.ExceptionType,
            MethodUsed = "Failed",
        };
    }

    private static SeekResult TrySeekViaCurrentFrame(object timeline, int targetFrame)
    {
        // setter 実行後に再読込し、実際に要求フレームへ到達したかを検証する。
        if (!TryReadCurrentFrame(timeline, out var beforeFrame, out var beforeReason, out var beforeExceptionType))
        {
            return SeekResult.Failed(
                targetFrame,
                beforeReason ?? "CurrentFrame getter unavailable",
                beforeFrame: 0,
                afterFrame: 0,
                methodUsed: "CurrentFrameProperty",
                exceptionType: beforeExceptionType,
                tolerance: FrameTolerance);
        }

        if (!TrySetCurrentFrame(timeline, targetFrame, out var setFailureReason, out var setExceptionType))
        {
            var afterFailureFrame = TryGetCurrentFrameOrFallback(timeline, beforeFrame);
            return SeekResult.Failed(
                targetFrame,
                setFailureReason ?? "CurrentFrame setter unavailable",
                beforeFrame: beforeFrame,
                afterFrame: afterFailureFrame,
                methodUsed: "CurrentFrameProperty",
                exceptionType: setExceptionType,
                tolerance: FrameTolerance);
        }

        if (!TryReadCurrentFrame(timeline, out var afterFrame, out var afterReason, out var afterExceptionType))
        {
            return SeekResult.Failed(
                targetFrame,
                afterReason ?? "CurrentFrame verification failed",
                beforeFrame: beforeFrame,
                afterFrame: beforeFrame,
                methodUsed: "CurrentFrameProperty",
                exceptionType: afterExceptionType,
                tolerance: FrameTolerance);
        }

        if (Math.Abs(afterFrame - targetFrame) > FrameTolerance)
        {
            return SeekResult.Failed(
                targetFrame,
                $"CurrentFrame verification failed. requested={targetFrame}, before={beforeFrame}, after={afterFrame}, tolerance={FrameTolerance}.",
                beforeFrame: beforeFrame,
                afterFrame: afterFrame,
                methodUsed: "CurrentFrameProperty",
                tolerance: FrameTolerance);
        }

        return SeekResult.Succeeded(targetFrame, beforeFrame, afterFrame, "CurrentFrameProperty", FrameTolerance);
    }

    private static SeekResult TrySeekViaCommandBindings(TimelineDiscoveryResult discovery, int targetFrame)
    {
        if (discovery.Timeline is null)
        {
            return SeekResult.Failed(
                targetFrame,
                "timeline not found for command binding fallback",
                methodUsed: "Failed",
                tolerance: FrameTolerance);
        }

        // CommandBinding は Window 側に登録されることが多いため、探索結果に近い Window を優先する。
        var windows = EnumerateCandidateWindows(discovery).ToArray();
        if (windows.Length == 0)
        {
            return SeekResult.Failed(
                targetFrame,
                "command binding fallback skipped: no candidate window found",
                beforeFrame: TryGetCurrentFrameOrFallback(discovery.Timeline, 0),
                afterFrame: TryGetCurrentFrameOrFallback(discovery.Timeline, 0),
                methodUsed: "Failed",
                tolerance: FrameTolerance);
        }

        var failureReasons = new List<string>();
        string? lastExceptionType = null;

        foreach (var window in windows)
        {
            var matchingBindings = window.CommandBindings
                .OfType<CommandBinding>()
                .Where(binding => MatchesCommandCandidate(binding.Command))
                .ToArray();

            if (matchingBindings.Length == 0)
            {
                continue;
            }

            foreach (var binding in matchingBindings)
            {
                var commandName = GetCommandName(binding.Command);
                foreach (var parameterAttempt in CreateParameterAttempts(targetFrame))
                {
                    var beforeFrame = TryGetCurrentFrameOrFallback(discovery.Timeline, 0);
                    var parameter = parameterAttempt.Create(beforeFrame);

                    try
                    {
                        if (!CanExecute(binding.Command, parameter, window))
                        {
                            failureReasons.Add($"{commandName} rejected {parameterAttempt.Label}");
                            continue;
                        }

                        Execute(binding.Command, parameter, window);

                        if (!TryReadCurrentFrame(discovery.Timeline, out var afterFrame, out var afterReason, out var afterExceptionType))
                        {
                            lastExceptionType = afterExceptionType;
                            failureReasons.Add($"{commandName} {parameterAttempt.Label}: {afterReason ?? "failed to read CurrentFrame after execute"}");
                            continue;
                        }

                        if (Math.Abs(afterFrame - targetFrame) <= FrameTolerance)
                        {
                            return SeekResult.Succeeded(
                                targetFrame,
                                beforeFrame,
                                afterFrame,
                                GetMethodNameForCommand(commandName),
                                FrameTolerance);
                        }

                        failureReasons.Add($"{commandName} {parameterAttempt.Label}: after={afterFrame} outside tolerance");
                    }
                    catch (Exception ex)
                    {
                        lastExceptionType = ex.GetType().FullName;
                        failureReasons.Add($"{commandName} {parameterAttempt.Label}: {ex.Message}");
                    }
                }
            }
        }

        return SeekResult.Failed(
            targetFrame,
            failureReasons.Count == 0
                ? "command binding fallback skipped: no matching command binding found"
                : string.Join(" | ", failureReasons),
            beforeFrame: TryGetCurrentFrameOrFallback(discovery.Timeline, 0),
            afterFrame: TryGetCurrentFrameOrFallback(discovery.Timeline, 0),
            methodUsed: "Failed",
            exceptionType: lastExceptionType,
            tolerance: FrameTolerance);
    }

    private static TimelineDiscoveryResult DiscoverTimeline(object? explicitTimeline)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return new TimelineDiscoveryResult(null, null, "Application.Current unavailable during timeline discovery.");
        }

        var queue = new Queue<DiscoveryNode>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var notes = new List<string>();

        // 明示引数、YMM のタイムラインコンテキスト、開いている Window の順に幅優先で探索する。
        Enqueue(queue, explicitTimeline, "SeekAsync.timeline", 0, null);
        var (timelineContextTimeline, timelineContextInfo) = TryGetTimelineContextCandidates();
        Enqueue(queue, timelineContextTimeline, "TimelineContextService.Timeline", 0, null);
        Enqueue(queue, timelineContextInfo, "TimelineContextService.Info", 0, null);

        foreach (Window window in app.Windows)
        {
            Enqueue(queue, window, $"Application.Current.Windows[{window.GetType().Name}]", 0, window);
        }

        var nodesVisited = 0;
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node.Value is null || !ShouldInspect(node.Value) || !visited.Add(node.Value))
            {
                continue;
            }

            nodesVisited++;
            if (HasCurrentFrameProperty(node.Value))
            {
                // CurrentFrame を持つオブジェクトをタイムライン候補として扱う。
                var source = $"{node.Path} ({node.Value.GetType().FullName})";
                return new TimelineDiscoveryResult(node.Value, node.OwnerWindow, $"timeline discovered from {source}. nodes={nodesVisited}");
            }

            if (node.Depth >= MaxDiscoveryDepth || nodesVisited >= MaxDiscoveryNodes)
            {
                continue;
            }

            foreach (var next in EnumerateChildren(node))
            {
                Enqueue(queue, next.Value, next.Path, next.Depth, next.OwnerWindow);
            }

            if (notes.Count < 12)
            {
                notes.Add($"{node.Path}:{node.Value.GetType().Name}");
            }
        }

        var limitReason = nodesVisited >= MaxDiscoveryNodes
            ? $"timeline not found after scanning {nodesVisited} nodes (limit {MaxDiscoveryNodes})"
            : $"timeline not found after scanning {nodesVisited} nodes";
        if (notes.Count > 0)
        {
            limitReason = $"{limitReason}. explored={string.Join(", ", notes)}";
        }

        return new TimelineDiscoveryResult(null, null, limitReason);
    }

    private static IEnumerable<DiscoveryNode> EnumerateChildren(DiscoveryNode node)
    {
        if (node.Value is FrameworkElement frameworkElement)
        {
            yield return node.CreateChild(frameworkElement.DataContext, $"{node.Path}.DataContext");
            yield return node.CreateChild(frameworkElement.ContentOrDefault(), $"{node.Path}.Content");
        }
        else if (node.Value is FrameworkContentElement frameworkContentElement)
        {
            yield return node.CreateChild(frameworkContentElement.DataContext, $"{node.Path}.DataContext");
        }
        else if (node.Value is Window window)
        {
            yield return node.CreateChild(window.DataContext, $"{node.Path}.DataContext", window);
            yield return node.CreateChild(window.Content, $"{node.Path}.Content", window);
        }

        if (node.Value is DependencyObject dependencyObject)
        {
            foreach (var child in EnumerateVisualChildren(node, dependencyObject))
            {
                yield return child;
            }

            foreach (var child in EnumerateLogicalChildren(node, dependencyObject))
            {
                yield return child;
            }
        }

        foreach (var child in EnumerateObjectProperties(node))
        {
            yield return child;
        }
    }

    private static IEnumerable<DiscoveryNode> EnumerateVisualChildren(DiscoveryNode node, DependencyObject dependencyObject)
    {
        int childCount;
        try
        {
            childCount = VisualTreeHelper.GetChildrenCount(dependencyObject);
        }
        catch
        {
            yield break;
        }

        for (var i = 0; i < childCount; i++)
        {
            DependencyObject? child;
            try
            {
                child = VisualTreeHelper.GetChild(dependencyObject, i);
            }
            catch
            {
                continue;
            }

            yield return node.CreateChild(child, $"{node.Path}.Visual[{i}]");
        }
    }

    private static IEnumerable<DiscoveryNode> EnumerateLogicalChildren(DiscoveryNode node, DependencyObject dependencyObject)
    {
        IEnumerator enumerator;
        try
        {
            enumerator = LogicalTreeHelper.GetChildren(dependencyObject).GetEnumerator();
        }
        catch
        {
            yield break;
        }

        using (enumerator as IDisposable)
        {
            var index = 0;
            while (true)
            {
                bool moved;
                try
                {
                    moved = enumerator.MoveNext();
                }
                catch
                {
                    yield break;
                }

                if (!moved)
                {
                    yield break;
                }

                yield return node.CreateChild(enumerator.Current, $"{node.Path}.Logical[{index++}]");
            }
        }
    }

    private static IEnumerable<DiscoveryNode> EnumerateObjectProperties(DiscoveryNode node)
    {
        var nodeValue = node.Value;
        if (nodeValue is null)
        {
            yield break;
        }

        // よく使うプロパティを先に見て、深いオブジェクトグラフを無駄に広げない。
        var properties = nodeValue.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .OrderBy(property => Array.IndexOf(PriorityPropertyNames, property.Name) switch
            {
                -1 => int.MaxValue,
                var index => index,
            })
            .ThenBy(property => property.Name, StringComparer.Ordinal);

        foreach (var property in properties)
        {
            if (!ShouldInspect(property.PropertyType))
            {
                continue;
            }

            object? value;
            try
            {
                value = property.GetValue(nodeValue);
            }
            catch
            {
                continue;
            }

            yield return node.CreateChild(value, $"{node.Path}.{property.Name}");
        }
    }

    private static IEnumerable<Window> EnumerateCandidateWindows(TimelineDiscoveryResult discovery)
    {
        var yielded = new HashSet<Window>(ReferenceEqualityComparer.Instance);
        if (discovery.OwnerWindow is not null && yielded.Add(discovery.OwnerWindow))
        {
            yield return discovery.OwnerWindow;
        }

        var app = System.Windows.Application.Current;
        if (app is null)
        {
            yield break;
        }

        foreach (Window window in app.Windows)
        {
            if (yielded.Add(window))
            {
                yield return window;
            }
        }
    }

    private static bool TrySetCurrentFrame(object timeline, int targetFrame, out string? failureReason, out string? exceptionType)
    {
        failureReason = null;
        exceptionType = null;

        var property = timeline.GetType().GetProperty("CurrentFrame", BindingFlags.Public | BindingFlags.Instance);
        if (property is null)
        {
            failureReason = "CurrentFrame property not found";
            return false;
        }

        if (!property.CanWrite)
        {
            failureReason = "CurrentFrame setter unavailable";
            return false;
        }

        try
        {
            var targetType = property.PropertyType;
            if (targetType == typeof(int))
            {
                property.SetValue(timeline, targetFrame);
                return true;
            }

            if (targetType == typeof(long))
            {
                property.SetValue(timeline, (long)targetFrame);
                return true;
            }

            var ctor = targetType.GetConstructor([typeof(int)]);
            if (ctor is not null)
            {
                property.SetValue(timeline, ctor.Invoke([targetFrame]));
                return true;
            }

            var frameProperty = targetType.GetProperty("Frame", BindingFlags.Public | BindingFlags.Instance);
            if (frameProperty is not null && frameProperty.CanWrite)
            {
                var instance = Activator.CreateInstance(targetType);
                if (instance is null)
                {
                    failureReason = $"failed to create {targetType.FullName}";
                    return false;
                }

                if (frameProperty.PropertyType == typeof(int))
                {
                    frameProperty.SetValue(instance, targetFrame);
                }
                else if (frameProperty.PropertyType == typeof(long))
                {
                    frameProperty.SetValue(instance, (long)targetFrame);
                }
                else
                {
                    failureReason = $"unsupported frame property type: {frameProperty.PropertyType.FullName}";
                    return false;
                }

                property.SetValue(timeline, instance);
                return true;
            }

            failureReason = $"unsupported CurrentFrame property type: {targetType.FullName}";
            return false;
        }
        catch (Exception ex)
        {
            var effectiveException = ex is TargetInvocationException { InnerException: Exception innerException }
                ? innerException
                : ex;
            exceptionType = effectiveException.GetType().FullName ?? effectiveException.GetType().Name;
            failureReason = effectiveException.Message;
            return false;
        }
    }

    private static bool TryReadCurrentFrame(object timeline, out int frame, out string? failureReason, out string? exceptionType)
    {
        frame = 0;
        failureReason = null;
        exceptionType = null;

        try
        {
            var property = timeline.GetType().GetProperty("CurrentFrame", BindingFlags.Public | BindingFlags.Instance);
            if (property is null || !property.CanRead)
            {
                failureReason = "CurrentFrame getter unavailable";
                return false;
            }

            if (!TryConvertFrameValue(property.GetValue(timeline), out frame))
            {
                failureReason = "CurrentFrame value could not be converted to an integer frame index";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            var effectiveException = ex is TargetInvocationException { InnerException: Exception innerException }
                ? innerException
                : ex;
            exceptionType = effectiveException.GetType().FullName ?? effectiveException.GetType().Name;
            failureReason = effectiveException.Message;
            return false;
        }
    }

    private static int TryGetCurrentFrameOrFallback(object timeline, int fallback)
        => TryReadCurrentFrame(timeline, out var frame, out _, out _) ? frame : fallback;

    private static bool TryConvertFrameValue(object? value, out int frame)
    {
        frame = 0;
        if (value is int intValue)
        {
            frame = intValue;
            return true;
        }

        if (value is long longValue)
        {
            frame = longValue > int.MaxValue ? int.MaxValue : (int)longValue;
            return true;
        }

        var frameProperty = value?.GetType().GetProperty("Frame", BindingFlags.Public | BindingFlags.Instance);
        if (frameProperty is null || !frameProperty.CanRead)
        {
            return false;
        }

        var frameValue = frameProperty.GetValue(value);
        if (frameValue is int frameInt)
        {
            frame = frameInt;
            return true;
        }

        if (frameValue is long frameLong)
        {
            frame = frameLong > int.MaxValue ? int.MaxValue : (int)frameLong;
            return true;
        }

        return false;
    }

    private static bool HasCurrentFrameProperty(object value)
    {
        var property = value.GetType().GetProperty("CurrentFrame", BindingFlags.Public | BindingFlags.Instance);
        return property is not null && property.CanRead;
    }

    private static bool MatchesCommandCandidate(ICommand? command)
    {
        var commandName = GetCommandName(command);
        return CommandNameCandidates.Any(candidate => commandName.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetCommandName(ICommand? command)
    {
        return command switch
        {
            RoutedUICommand routedUiCommand when !string.IsNullOrWhiteSpace(routedUiCommand.Name) => routedUiCommand.Name,
            RoutedUICommand routedUiCommand when !string.IsNullOrWhiteSpace(routedUiCommand.Text) => routedUiCommand.Text,
            RoutedCommand routedCommand when !string.IsNullOrWhiteSpace(routedCommand.Name) => routedCommand.Name,
            null => string.Empty,
            _ => command.GetType().Name,
        };
    }

    private static string GetMethodNameForCommand(string commandName)
    {
        if (commandName.Contains("ScrollToFrame", StringComparison.OrdinalIgnoreCase))
        {
            return "CommandBindingScrollToFrame";
        }

        return "CommandBindingSeek";
    }

    private static bool CanExecute(ICommand command, object? parameter, Window window)
    {
        return command switch
        {
            RoutedCommand routedCommand => routedCommand.CanExecute(parameter, window),
            _ => command.CanExecute(parameter),
        };
    }

    private static void Execute(ICommand command, object? parameter, Window window)
    {
        switch (command)
        {
            case RoutedCommand routedCommand:
                routedCommand.Execute(parameter, window);
                break;
            default:
                command.Execute(parameter);
                break;
        }
    }

    private static IEnumerable<CommandParameterAttempt> CreateParameterAttempts(int targetFrame)
    {
        yield return new CommandParameterAttempt("targetFrame", _ => targetFrame);
        yield return new CommandParameterAttempt("timeSpan", _ => TimeSpan.FromMilliseconds(targetFrame));
        yield return new CommandParameterAttempt("frameDelta", currentFrame => targetFrame - currentFrame);
        yield return new CommandParameterAttempt("null", _ => null);
    }

    private static string? CombineReasons(params string?[] reasons)
    {
        var filtered = reasons
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Select(reason => reason!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return filtered.Length == 0 ? null : string.Join(" | ", filtered);
    }

    private static bool ShouldInspect(object value) => ShouldInspect(value.GetType());

    private static bool ShouldInspect(Type type)
    {
        if (type.IsPrimitive || type.IsEnum)
        {
            return false;
        }

        if (type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan)
            || type == typeof(Guid)
            || type == typeof(Uri))
        {
            return false;
        }

        return true;
    }

    private static (object? Timeline, object? Info) TryGetTimelineContextCandidates()
    {
        try
        {
            var timelineContextType = Type.GetType("YMMProjectManager.Infrastructure.TimelineContextService, YMMProjectManager", throwOnError: false);
            if (timelineContextType is null)
            {
                return (null, null);
            }

            var timeline = timelineContextType.GetProperty("Timeline", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var info = timelineContextType.GetProperty("Info", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            return (timeline, info);
        }
        catch
        {
            return (null, null);
        }
    }

    private sealed record DiscoveryNode(object? Value, string Path, int Depth, Window? OwnerWindow)
    {
        public DiscoveryNode CreateChild(object? value, string path, Window? ownerWindow = null)
            => new(value, path, Depth + 1, ownerWindow ?? OwnerWindow);
    }

    private sealed record TimelineDiscoveryResult(object? Timeline, Window? OwnerWindow, string? FailureReason);

    private sealed record CommandParameterAttempt(string Label, Func<int, object?> Create);

    private static void Enqueue(Queue<DiscoveryNode> queue, object? value, string path, int depth, Window? ownerWindow)
    {
        if (value is null)
        {
            return;
        }

        queue.Enqueue(new DiscoveryNode(value, path, depth, ownerWindow));
    }
}

internal static class FrameworkElementExtensions
{
    public static object? ContentOrDefault(this FrameworkElement element)
        => element.GetType().GetProperty("Content", BindingFlags.Public | BindingFlags.Instance)?.GetValue(element);
}
