using YMMProjectManager.Domain;

namespace YMMProjectManager.Infrastructure.Checkpoint;

public sealed class ThumbnailIntervalPlanner
{
    public CheckpointThumbnailPlan CreatePlan(CheckpointThumbnailSettings settings, int totalFrames, int framesPerSecond)
    {
        var normalizedFps = Math.Max(1, framesPerSecond);
        var lastFrame = Math.Max(0, totalFrames - 1);
        var frames = settings.Mode switch
        {
            CheckpointThumbnailMode.EvenSplit => CreateEvenSplitFrames(settings.SampleCount, lastFrame),
            CheckpointThumbnailMode.Every1Second => CreateIntervalFrames(normalizedFps, lastFrame, settings.IncludeLastFrame),
            CheckpointThumbnailMode.Every5Seconds => CreateIntervalFrames(normalizedFps * 5, lastFrame, settings.IncludeLastFrame),
            CheckpointThumbnailMode.Every10Seconds => CreateIntervalFrames(normalizedFps * 10, lastFrame, settings.IncludeLastFrame),
            CheckpointThumbnailMode.Every30Seconds => CreateIntervalFrames(normalizedFps * 30, lastFrame, settings.IncludeLastFrame),
            CheckpointThumbnailMode.Every1Minute => CreateIntervalFrames(normalizedFps * 60, lastFrame, settings.IncludeLastFrame),
            CheckpointThumbnailMode.Every5Minutes => CreateIntervalFrames(normalizedFps * 300, lastFrame, settings.IncludeLastFrame),
            CheckpointThumbnailMode.CustomSeconds => CreateIntervalFrames(normalizedFps * Math.Max(1, settings.CustomValue), lastFrame, settings.IncludeLastFrame),
            CheckpointThumbnailMode.CustomMinutes => CreateIntervalFrames(normalizedFps * 60 * Math.Max(1, settings.CustomValue), lastFrame, settings.IncludeLastFrame),
            _ => CreateEvenSplitFrames(settings.SampleCount, lastFrame),
        };

        return new CheckpointThumbnailPlan
        {
            Frames = frames,
            ModeLabel = GetModeLabel(settings),
            CustomValue = settings.Mode is CheckpointThumbnailMode.CustomSeconds or CheckpointThumbnailMode.CustomMinutes ? Math.Max(1, settings.CustomValue) : 0,
        };
    }

    public string GetModeLabel(CheckpointThumbnailSettings settings)
    {
        return settings.Mode switch
        {
            CheckpointThumbnailMode.EvenSplit => $"均等分割({Math.Max(1, settings.SampleCount)}件)",
            CheckpointThumbnailMode.Every1Second => "1秒ごと",
            CheckpointThumbnailMode.Every5Seconds => "5秒ごと",
            CheckpointThumbnailMode.Every10Seconds => "10秒ごと",
            CheckpointThumbnailMode.Every30Seconds => "30秒ごと",
            CheckpointThumbnailMode.Every1Minute => "1分ごと",
            CheckpointThumbnailMode.Every5Minutes => "5分ごと",
            CheckpointThumbnailMode.CustomSeconds => $"任意秒数({Math.Max(1, settings.CustomValue)}秒ごと)",
            CheckpointThumbnailMode.CustomMinutes => $"任意分数({Math.Max(1, settings.CustomValue)}分ごと)",
            _ => "均等分割",
        };
    }

    private static int[] CreateEvenSplitFrames(int sampleCount, int lastFrame)
    {
        var normalizedCount = Math.Max(1, sampleCount);
        var frames = new int[normalizedCount];
        for (var i = 0; i < normalizedCount; i++)
        {
            frames[i] = lastFrame == 0 ? 0 : (int)Math.Floor(i * (lastFrame / (double)Math.Max(1, normalizedCount - 1)));
        }

        return frames.Distinct().ToArray();
    }

    private static int[] CreateIntervalFrames(int intervalFrames, int lastFrame, bool includeLastFrame)
    {
        var normalizedInterval = Math.Max(1, intervalFrames);
        var frames = new List<int>();
        for (var frame = 0; frame <= lastFrame; frame += normalizedInterval)
        {
            frames.Add(frame);
        }

        if (includeLastFrame && (frames.Count == 0 || frames[^1] != lastFrame))
        {
            frames.Add(lastFrame);
        }

        return frames.Distinct().OrderBy(x => x).ToArray();
    }
}
