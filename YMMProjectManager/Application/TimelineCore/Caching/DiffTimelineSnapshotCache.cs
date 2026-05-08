using System.Security.Cryptography;
using System.Text;

namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelineSnapshotCacheKey(string Value);

public interface IDiffTimelineSnapshotCache
{
    bool TryGet(DiffTimelineSnapshotCacheKey key, out DiffTimelineStandalonePipelineResult? result);
    void Set(DiffTimelineSnapshotCacheKey key, DiffTimelineStandalonePipelineResult result);
}

public static class DiffTimelineSnapshotCacheKeyFactory
{
    public static DiffTimelineSnapshotCacheKey Create(
        DiffTimelineProjectSnapshot oldSnapshot,
        DiffTimelineProjectSnapshot newSnapshot,
        IReadOnlyDictionary<string, string>? options)
    {
        var optionsHash = options is null
            ? "none"
            : ComputeHash(string.Join(";", options.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value}")));
        var value = $"{oldSnapshot.Metadata.SnapshotHash}:{newSnapshot.Metadata.SnapshotHash}:{optionsHash}";
        return new DiffTimelineSnapshotCacheKey(value);
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }
}

public sealed class InMemoryDiffTimelineSnapshotCache : IDiffTimelineSnapshotCache
{
    private readonly Dictionary<string, DiffTimelineStandalonePipelineResult> cache = new(StringComparer.Ordinal);

    public bool TryGet(DiffTimelineSnapshotCacheKey key, out DiffTimelineStandalonePipelineResult? result)
    {
        if (cache.TryGetValue(key.Value, out var value))
        {
            result = value;
            return true;
        }

        result = null;
        return false;
    }

    public void Set(DiffTimelineSnapshotCacheKey key, DiffTimelineStandalonePipelineResult result)
    {
        cache[key.Value] = result;
    }
}
