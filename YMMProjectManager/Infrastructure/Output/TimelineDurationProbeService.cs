using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Infrastructure.Output;

public sealed class TimelineDurationProbeService
{
    private readonly FileLogger logger;

    public TimelineDurationProbeService(FileLogger logger)
    {
        this.logger = logger;
    }

    public async Task<TimelineDurationProbeResult> WriteTimelineDurationProbeAsync(CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        var sw = Stopwatch.StartNew();
        // Store probe output outside the repo so repeated manual checks do not dirty the working tree.
        var outputDirectory = Path.Combine(Path.GetTempPath(), "YMMProjectManager", "timeline-duration-probe");
        Directory.CreateDirectory(outputDirectory);
        var stamp = started.ToString("yyyyMMdd-HHmmss");
        var jsonPath = Path.Combine(outputDirectory, $"timeline-duration-probe-{stamp}.json");

        var result = new TimelineDurationProbeResult
        {
            CurrentFrame = GetCurrentFrame(TimelineContextService.Timeline),
        };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var timeline = TimelineContextService.Timeline;
            if (timeline is null)
            {
                result.Success = false;
                result.MethodUsed = "Failed";
                result.FailureReason = "Timeline not found.";
                return await FinalizeAsync(result, jsonPath, sw).ConfigureAwait(true);
            }

            if (TryResolveLastFrame(timeline, out var lastFrame, out var methodUsed, out var candidates))
            {
                result.Success = true;
                result.LastFrame = lastFrame;
                result.MethodUsed = methodUsed;
                result.CandidateProperties = candidates.ToArray();
                return await FinalizeAsync(result, jsonPath, sw).ConfigureAwait(true);
            }

            result.Success = false;
            result.MethodUsed = "Failed";
            result.CandidateProperties = candidates.ToArray();
            result.FailureReason = "No usable timeline length candidate was found.";
            return await FinalizeAsync(result, jsonPath, sw).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.MethodUsed = "Failed";
            result.FailureReason = ex.Message;
            return await FinalizeAsync(result, jsonPath, sw).ConfigureAwait(true);
        }
    }

    private static int GetCurrentFrame(object? timeline)
    {
        if (timeline is null)
        {
            return 0;
        }

        var value = timeline.GetType().GetProperty("CurrentFrame", BindingFlags.Public | BindingFlags.Instance)?.GetValue(timeline);
        return TryConvertToInt(value, out var frame) ? frame : 0;
    }

    private bool TryResolveLastFrame(object timeline, out int lastFrame, out string methodUsed, out List<string> candidates)
    {
        candidates = [];
        lastFrame = 0;
        methodUsed = "Failed";

        // Prefer explicit frame-count properties first; they are the least ambiguous when present.
        foreach (var propertyName in new[] { "Length", "TotalFrame", "FrameCount", "EndFrame" })
        {
            candidates.Add($"Timeline.{propertyName}");
            if (!TryReadIntLikeProperty(timeline, propertyName, out var value))
            {
                continue;
            }

            if (!propertyName.Equals("EndFrame", StringComparison.OrdinalIgnoreCase) && value <= 0)
            {
                continue;
            }

            lastFrame = propertyName.Equals("EndFrame", StringComparison.OrdinalIgnoreCase)
                ? Math.Max(0, value)
                : Math.Max(0, value - 1);
            methodUsed = $"Timeline.{propertyName}";
            return true;
        }

        candidates.Add("Timeline.VideoInfo");
        if (TryResolveFromVideoInfo(timeline, out lastFrame))
        {
            methodUsed = "Timeline.VideoInfo";
            return true;
        }

        candidates.Add("Timeline.Items(max end)");
        if (TryResolveFromItems(timeline, out lastFrame))
        {
            methodUsed = "Timeline.Items(max end)";
            return true;
        }

        return false;
    }

    private static bool TryResolveFromVideoInfo(object timeline, out int lastFrame)
    {
        lastFrame = 0;
        // VideoInfo is the fallback when Timeline itself does not expose a direct frame count.
        var videoInfo = timeline.GetType().GetProperty("VideoInfo", BindingFlags.Public | BindingFlags.Instance)?.GetValue(timeline);
        if (videoInfo is null)
        {
            return false;
        }

        foreach (var propertyName in new[] { "Length", "TotalFrame", "FrameCount" })
        {
            if (!TryReadIntLikeProperty(videoInfo, propertyName, out var value))
            {
                continue;
            }

            if (value <= 0)
            {
                continue;
            }

            lastFrame = Math.Max(0, value - 1);
            return true;
        }

        if (TryReadTimeSpanProperty(videoInfo, "Duration", out var duration) && duration > TimeSpan.Zero)
        {
            var fps = GetTimelineFps(timeline);
            if (fps > 0)
            {
                lastFrame = Math.Max(0, (int)Math.Round(duration.TotalSeconds * fps) - 1);
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveFromItems(object timeline, out int lastFrame)
    {
        lastFrame = 0;
        // Item ranges are the last fallback: compute the maximum end frame from every timeline item.
        var items = timeline.GetType().GetProperty("Items", BindingFlags.Public | BindingFlags.Instance)?.GetValue(timeline) as System.Collections.IEnumerable;
        if (items is null)
        {
            return false;
        }

        var maxEnd = -1;
        foreach (var item in items)
        {
            if (item is null)
            {
                continue;
            }

            if (TryReadIntLikeProperty(item, "EndFrame", out var endFrame))
            {
                maxEnd = Math.Max(maxEnd, endFrame);
                continue;
            }

            if (!TryReadIntLikeProperty(item, "Frame", out var frame))
            {
                continue;
            }

            if (TryReadIntLikeProperty(item, "Length", out var length) || TryReadIntLikeProperty(item, "Duration", out length))
            {
                maxEnd = Math.Max(maxEnd, frame + length);
            }
        }

        if (maxEnd < 0)
        {
            return false;
        }

        lastFrame = Math.Max(0, maxEnd - 1);
        return true;
    }

    private static bool TryReadIntLikeProperty(object instance, string propertyName, out int value)
    {
        value = 0;
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property is null)
        {
            return false;
        }

        var raw = property.GetValue(instance);
        return TryConvertToInt(raw, out value);
    }

    private static bool TryReadTimeSpanProperty(object instance, string propertyName, out TimeSpan value)
    {
        value = default;
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property is null)
        {
            return false;
        }

        var raw = property.GetValue(instance);
        if (raw is TimeSpan ts)
        {
            value = ts;
            return true;
        }

        if (raw is string s && TimeSpan.TryParse(s, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool TryConvertToInt(object? raw, out int value)
    {
        value = 0;
        switch (raw)
        {
            case int i:
                value = i;
                return true;
            case long l:
                value = l > int.MaxValue ? int.MaxValue : (int)l;
                return true;
            case short s:
                value = s;
                return true;
            case byte b:
                value = b;
                return true;
            case decimal m:
                value = m > int.MaxValue ? int.MaxValue : (int)m;
                return true;
            case double d:
                if (double.IsNaN(d) || double.IsInfinity(d))
                {
                    return false;
                }

                value = d > int.MaxValue ? int.MaxValue : d < int.MinValue ? int.MinValue : (int)Math.Round(d);
                return true;
            case float f:
                if (float.IsNaN(f) || float.IsInfinity(f))
                {
                    return false;
                }

                value = f > int.MaxValue ? int.MaxValue : f < int.MinValue ? int.MinValue : (int)Math.Round(f);
                return true;
            case string s when int.TryParse(s, out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    private static double GetTimelineFps(object timeline)
    {
        try
        {
            var videoInfoProperty = timeline.GetType().GetProperty("VideoInfo", BindingFlags.Public | BindingFlags.Instance);
            var videoInfo = videoInfoProperty?.GetValue(timeline);
            if (videoInfo is not null)
            {
                var fpsValue = GetPropertyDouble(videoInfo, "FPS");
                if (fpsValue > 0)
                {
                    return fpsValue;
                }
            }

            var directFps = GetPropertyDouble(timeline, "FPS");
            if (directFps > 0)
            {
                return directFps;
            }

            var frameRate = GetPropertyDouble(timeline, "FrameRate");
            if (frameRate > 0)
            {
                return frameRate;
            }
        }
        catch
        {
            // fallback below
        }

        return 60.0;
    }

    private static double GetPropertyDouble(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property is null)
        {
            return 0;
        }

        var value = property.GetValue(instance);
        return value switch
        {
            double d => d,
            float f => f,
            decimal m => (double)m,
            int i => i,
            long l => l,
            string s when double.TryParse(s, out var parsed) => parsed,
            _ => 0,
        };
    }

    private async Task<TimelineDurationProbeResult> FinalizeAsync(TimelineDurationProbeResult result, string jsonPath, Stopwatch sw)
    {
        sw.Stop();
        result.DurationMs = sw.ElapsedMilliseconds;

        try
        {
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(jsonPath, json).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to write timeline duration diagnostics.");
        }

        logger.Info($"Timeline duration probe end. success={result.Success}, method={result.MethodUsed}, lastFrame={result.LastFrame}, reason={result.FailureReason ?? string.Empty}");
        logger.Flush();
        return result;
    }
}

public sealed class TimelineDurationProbeResult
{
    public bool Success { get; set; }
    public int CurrentFrame { get; set; }
    public int LastFrame { get; set; }
    public string MethodUsed { get; set; } = string.Empty;
    public string? FailureReason { get; set; }
    public string[] CandidateProperties { get; set; } = [];
    public long DurationMs { get; set; }
}
