using System.Text.Json;

namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineValidationRunHistoryWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Append(string directory, DiffTimelineValidationRunRecord record, int keepLast = 50)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "difftimeline-validation-run-history.json");
        var history = Load(path).Runs.ToList();
        history.Add(record);
        if (history.Count > keepLast)
        {
            history = history.Skip(history.Count - keepLast).ToList();
        }

        File.WriteAllText(path, JsonSerializer.Serialize(new DiffTimelineValidationRunHistory(history), JsonOptions));
        return path;
    }

    public static DiffTimelineValidationRunHistory Load(string path)
    {
        if (!File.Exists(path))
        {
            return new DiffTimelineValidationRunHistory([]);
        }

        var text = File.ReadAllText(path);
        var parsed = JsonSerializer.Deserialize<DiffTimelineValidationRunHistory>(text, JsonOptions);
        return parsed ?? new DiffTimelineValidationRunHistory([]);
    }
}
