using System.Security.Cryptography;
using System.Text;
using YMMProjectManager.Infrastructure.Ymm;

namespace YMMProjectManager.Application.TimelineCore;

public sealed class YmmNormalizedJsonSnapshotAdapter
{
    private readonly YmmProjectParser parser;
    private readonly Action<string>? diagnosticsSink;

    public YmmNormalizedJsonSnapshotAdapter(Action<string>? diagnosticsSink = null)
    {
        parser = new YmmProjectParser();
        this.diagnosticsSink = diagnosticsSink;
    }

    public DiffTimelineProjectSnapshot Convert(string projectId, string projectName, string sourcePath, string normalizedJson)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var model = parser.Parse(normalizedJson);
        var groupedByTimeline = model.Items.GroupBy(x => x.TimelineIndex).OrderBy(x => x.Key);
        var timelines = new List<DiffTimelineTimelineSnapshot>();

        foreach (var timelineGroup in groupedByTimeline)
        {
            var layers = timelineGroup
                .GroupBy(x => x.Layer)
                .OrderBy(x => x.Key)
                .Select(layerGroup => new DiffTimelineLayerSnapshot(
                    LayerId: $"layer-{timelineGroup.Key}-{layerGroup.Key}",
                    LayerName: $"Layer {layerGroup.Key}",
                    LayerOrder: layerGroup.Key,
                    Items: layerGroup.Select(ToItemSnapshot).ToList(),
                    DiagnosticsMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["itemCount"] = layerGroup.Count().ToString(),
                    }))
                .ToList();

            timelines.Add(new DiffTimelineTimelineSnapshot(
                TimelineId: $"timeline-{timelineGroup.Key}",
                TimelineName: $"Timeline {timelineGroup.Key}",
                TimelineOrder: timelineGroup.Key,
                Layers: layers,
                DiagnosticsMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["itemCount"] = timelineGroup.Count().ToString(),
                }));
        }

        var hash = ComputeHash(normalizedJson);
        sw.Stop();
        diagnosticsSink?.Invoke($"adapter=YmmNormalizedJsonSnapshotAdapter path={sourcePath} timelines={timelines.Count} items={model.Items.Count} hash={hash} elapsedMs={sw.ElapsedMilliseconds}");
        return new DiffTimelineProjectSnapshot(
            ProjectId: projectId,
            ProjectName: projectName,
            Timelines: timelines,
            Metadata: new DiffTimelineSnapshotMetadata(
                SchemaVersion: "1.0",
                SourceKind: "normalized-json",
                SourcePath: sourcePath,
                CapturedAt: DateTimeOffset.UtcNow,
                SnapshotHash: hash,
                DiagnosticsMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["adapter"] = nameof(YmmNormalizedJsonSnapshotAdapter),
                    ["normalizedJsonSource"] = sourcePath,
                    ["timelineCount"] = timelines.Count.ToString(),
                    ["itemCount"] = model.Items.Count.ToString(),
                    ["propertyCount"] = model.Items.Sum(x => x.Fields.Count).ToString(),
                    ["unsupportedFieldCount"] = "0",
                    ["skippedFieldCount"] = "0",
                    ["adapterDurationMs"] = sw.ElapsedMilliseconds.ToString(),
                }));
    }

    private static DiffTimelineItemSnapshot ToItemSnapshot(YmmItemModel item)
    {
        var properties = item.Fields
            .Select(kv => new DiffTimelineItemPropertySnapshot(
                Name: kv.Key,
                ValueType: "string",
                StringValue: kv.Value,
                NumericValue: null,
                BooleanValue: null,
                TimeValue: null,
                DiagnosticsMetadata: new Dictionary<string, string>()))
            .ToList();

        return new DiffTimelineItemSnapshot(
            ItemId: string.IsNullOrWhiteSpace(item.InternalId) ? Guid.NewGuid().ToString("N") : item.InternalId!,
            DisplayName: item.Text ?? item.Type ?? "(item)",
            TimelineIndex: item.TimelineIndex,
            Layer: item.Layer,
            Frame: item.Frame,
            Length: Math.Max(1, item.Length),
            Properties: properties,
            DiagnosticsMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["scope"] = item.Scope,
                ["type"] = item.Type ?? string.Empty,
            });
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return System.Convert.ToHexString(bytes);
    }
}
