using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace YMMProjectManager.Infrastructure.Thumbnails;

public sealed class YmmTimelineSeekAdapter
{
    public async Task<SeekResult> SeekAsync(object? timeline, int targetFrame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (timeline is null)
        {
            return SeekResult.Failed("timeline not found");
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return SeekResult.Failed("dispatcher unavailable");
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await dispatcher.InvokeAsync(() =>
            {
                var before = GetCurrentFrame(timeline);
                if (!TrySetCurrentFrame(timeline, targetFrame))
                {
                    return SeekResult.Failed("CurrentFrame setter unavailable", before, GetCurrentFrame(timeline));
                }

                var after = GetCurrentFrame(timeline);
                return SeekResult.Succeeded(before, after);
            }, DispatcherPriority.Send, cancellationToken).Task.ConfigureAwait(true);

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
            return SeekResult.Failed(ex.Message, duration: sw.Elapsed);
        }
    }

    private static bool TrySetCurrentFrame(object timeline, int targetFrame)
    {
        var property = timeline.GetType().GetProperty("CurrentFrame", BindingFlags.Public | BindingFlags.Instance);
        if (property is null || !property.CanWrite)
        {
            return false;
        }

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
                return false;
            }

            property.SetValue(timeline, instance);
            return true;
        }

        return false;
    }

    private static int GetCurrentFrame(object timeline)
    {
        var value = timeline.GetType().GetProperty("CurrentFrame", BindingFlags.Public | BindingFlags.Instance)?.GetValue(timeline);
        if (value is int intValue)
        {
            return intValue;
        }

        if (value is long longValue)
        {
            return longValue > int.MaxValue ? int.MaxValue : (int)longValue;
        }

        var frameProperty = value?.GetType().GetProperty("Frame", BindingFlags.Public | BindingFlags.Instance);
        var frameValue = frameProperty?.GetValue(value);
        if (frameValue is int frameInt)
        {
            return frameInt;
        }

        if (frameValue is long frameLong)
        {
            return frameLong > int.MaxValue ? int.MaxValue : (int)frameLong;
        }

        return 0;
    }
}

public sealed record SeekResult(bool Success, int BeforeFrame, int AfterFrame, string? Reason, TimeSpan Duration)
{
    public static SeekResult Succeeded(int beforeFrame, int afterFrame) => new(true, beforeFrame, afterFrame, null, TimeSpan.Zero);

    public static SeekResult Failed(string reason, int beforeFrame = 0, int afterFrame = 0, TimeSpan? duration = null)
        => new(false, beforeFrame, afterFrame, reason, duration ?? TimeSpan.Zero);
}
