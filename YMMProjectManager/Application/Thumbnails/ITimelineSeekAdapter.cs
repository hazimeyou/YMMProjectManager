using System.IO;

namespace YMMProjectManager.Application.Thumbnails;

public interface ITimelineSeekAdapter
{
    Task<SeekResult> SeekAsync(object? timeline, int targetFrame, CancellationToken cancellationToken);

    async Task<string> WriteSeekProbeAsync(object? timeline, int targetFrame, string outputDirectory, CancellationToken cancellationToken)
    {
        var result = await SeekAsync(timeline, targetFrame, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, $"seek-probe-{DateTime.Now:yyyyMMdd-HHmmss-fff}-frame-{targetFrame:D6}.json");
        var json = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
        return path;
    }
}
