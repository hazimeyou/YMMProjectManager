using System.Text.Json;

namespace YMMProjectManager.Application.TimelineCore;

public sealed class DiffTimelineSnapshotJsonSerializer : IDiffTimelineSnapshotSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public string Serialize(DiffTimelineProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, Options);
    }

    public DiffTimelineProjectSnapshot Deserialize(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Snapshot JSON content is empty.", nameof(content));
        }

        var snapshot = JsonSerializer.Deserialize<DiffTimelineProjectSnapshot>(content, Options);
        if (snapshot is null)
        {
            throw new InvalidOperationException("Failed to deserialize DiffTimelineProjectSnapshot from JSON.");
        }

        return snapshot;
    }
}
