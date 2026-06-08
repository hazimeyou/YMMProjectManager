namespace YMMProjectManager.Infrastructure.Thumbnails;

public static class FastThumbnailFrameSampler
{
    public static int[] CreateSampleFrames(int sampleCount, int startFrame, int endFrame)
    {
        if (sampleCount <= 0)
        {
            return [];
        }

        var normalizedStart = Math.Min(startFrame, endFrame);
        var normalizedEnd = Math.Max(startFrame, endFrame);
        var frames = new int[sampleCount];

        for (var i = 0; i < sampleCount; i++)
        {
            var ratio = sampleCount == 1 ? 0d : (double)i / (sampleCount - 1);
            frames[i] = (int)Math.Round(normalizedStart + (normalizedEnd - normalizedStart) * ratio, MidpointRounding.AwayFromZero);
        }

        return frames;
    }
}
