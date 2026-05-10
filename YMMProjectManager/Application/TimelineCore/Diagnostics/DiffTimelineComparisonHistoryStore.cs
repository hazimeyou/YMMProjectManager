namespace YMMProjectManager.Application.TimelineCore;

public sealed class DiffTimelineComparisonHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string storagePath;

    public DiffTimelineComparisonHistoryStore(string storageDirectory)
    {
        Directory.CreateDirectory(storageDirectory);
        storagePath = Path.Combine(storageDirectory, "difftimeline-comparison-history.json");
    }

    public IReadOnlyList<DiffTimelineComparisonHistoryEntry> Load()
    {
        if (!File.Exists(storagePath))
        {
            return [];
        }

        var json = File.ReadAllText(storagePath);
        return JsonSerializer.Deserialize<List<DiffTimelineComparisonHistoryEntry>>(json, JsonOptions) ?? [];
    }

    public void Append(DiffTimelineComparisonHistoryEntry entry, int keepLast = 100)
    {
        var list = Load().ToList();
        list.Add(entry);
        if (list.Count > keepLast)
        {
            list = list.Skip(list.Count - keepLast).ToList();
        }

        File.WriteAllText(storagePath, JsonSerializer.Serialize(list, JsonOptions));
    }
}
