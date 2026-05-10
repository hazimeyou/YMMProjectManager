namespace YMMProjectManager.Infrastructure.Diff;

public sealed class JsonDiffService
{
    public IReadOnlyList<JsonDiffEntry> Diff(string beforeJson, string afterJson)
    {
        var before = JsonNode.Parse(beforeJson);
        var after = JsonNode.Parse(afterJson);
        var diffs = new List<JsonDiffEntry>();
        WalkDiff(before, after, "$", diffs);
        return diffs;
    }

    private static void WalkDiff(JsonNode? before, JsonNode? after, string path, List<JsonDiffEntry> diffs)
    {
        if (before is null && after is null)
        {
            return;
        }

        if (before is null)
        {
            diffs.Add(new JsonDiffEntry { Kind = JsonDiffKind.Added, Path = path, After = after?.ToJsonString() });
            return;
        }

        if (after is null)
        {
            diffs.Add(new JsonDiffEntry { Kind = JsonDiffKind.Removed, Path = path, Before = before.ToJsonString() });
            return;
        }

        if (before is JsonValue || after is JsonValue)
        {
            if (!JsonNode.DeepEquals(before, after))
            {
                diffs.Add(new JsonDiffEntry
                {
                    Kind = JsonDiffKind.Changed,
                    Path = path,
                    Before = before.ToJsonString(),
                    After = after.ToJsonString(),
                });
            }

            return;
        }

        if (before is JsonArray bArr && after is JsonArray aArr)
        {
            var max = Math.Max(bArr.Count, aArr.Count);
            for (var i = 0; i < max; i++)
            {
                var b = i < bArr.Count ? bArr[i] : null;
                var a = i < aArr.Count ? aArr[i] : null;
                WalkDiff(b, a, $"{path}[{i}]", diffs);
            }

            return;
        }

        if (before is JsonObject bObj && after is JsonObject aObj)
        {
            var keys = bObj.Select(x => x.Key).Union(aObj.Select(x => x.Key), StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
            foreach (var key in keys)
            {
                bObj.TryGetPropertyValue(key, out var b);
                aObj.TryGetPropertyValue(key, out var a);
                WalkDiff(b, a, $"{path}.{key}", diffs);
            }

            return;
        }

        if (!JsonNode.DeepEquals(before, after))
        {
            diffs.Add(new JsonDiffEntry
            {
                Kind = JsonDiffKind.Changed,
                Path = path,
                Before = before.ToJsonString(),
                After = after.ToJsonString(),
            });
        }
    }
}
