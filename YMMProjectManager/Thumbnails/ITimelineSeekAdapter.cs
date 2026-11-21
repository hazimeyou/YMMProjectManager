using System.IO;

namespace YMMProjectManager.Application.Thumbnails;

/// <summary>
/// YMM のタイムラインを指定フレームへ移動するための抽象化です。
/// </summary>
public interface ITimelineSeekAdapter
{
    /// <summary>
    /// 指定されたタイムラインを絶対フレーム位置へ移動します。
    /// </summary>
    Task<SeekResult> SeekAsync(object? timeline, int targetFrame, CancellationToken cancellationToken);

    /// <summary>
    /// シーク処理を実行し、結果を調査用 JSON として保存します。
    /// </summary>
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
