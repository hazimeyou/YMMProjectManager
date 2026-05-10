namespace YMMProjectManager.Infrastructure.Diff;

public sealed class JsonNormalizeService
{
    private readonly JsonNormalizeOptions options;

    public JsonNormalizeService(JsonNormalizeOptions? options = null)
    {
        this.options = options ?? new JsonNormalizeOptions();
    }

    public async Task<string> NormalizeFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var raw = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        return NormalizeJson(raw);
    }

    public string NormalizeJson(string rawJson)
    {
        var node = JsonNode.Parse(rawJson);
        var normalized = NormalizeNode(node);
        return normalized?.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) ?? "null";
    }

    private JsonNode? NormalizeNode(JsonNode? node)
    {
        return node switch
        {
            JsonObject obj => NormalizeObject(obj),
            JsonArray arr => NormalizeArray(arr),
            _ => node?.DeepClone(),
        };
    }

    private JsonNode NormalizeObject(JsonObject obj)
    {
        var result = new JsonObject();
        var properties = options.SortProperties
            ? obj.OrderBy(x => x.Key, StringComparer.Ordinal)
            : obj.AsEnumerable();
        foreach (var kv in properties)
        {
            result[kv.Key] = NormalizeNode(kv.Value);
        }

        return result;
    }

    private JsonNode NormalizeArray(JsonArray arr)
    {
        var result = new JsonArray();
        foreach (var item in arr)
        {
            result.Add(NormalizeNode(item));
        }

        return result;
    }
}
