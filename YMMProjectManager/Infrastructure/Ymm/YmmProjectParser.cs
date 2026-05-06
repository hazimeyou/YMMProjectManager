using System;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace YMMProjectManager.Infrastructure.Ymm;

public sealed class YmmProjectParser
{
    private static readonly string[] TargetFields = ["Text", "FilePath", "Frame", "Layer", "Length", "Type"];
    private static readonly Regex TimelineIndexRegex = new(@"\.Timelines\[(\d+)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly InternalItemIdService internalItemIdService;

    public YmmProjectParser(InternalItemIdService? internalItemIdService = null)
    {
        this.internalItemIdService = internalItemIdService ?? new InternalItemIdService();
    }

    public YmmTimelineModel Parse(string normalizedJson)
    {
        var root = JsonNode.Parse(normalizedJson);
        var model = new YmmTimelineModel();
        Visit(root, "$", model);
        return model;
    }

    private void Visit(JsonNode? node, string scope, YmmTimelineModel timeline)
    {
        if (node is JsonObject obj)
        {
            var item = new YmmItemModel { Scope = scope, TimelineIndex = TryExtractTimelineIndex(scope) };
            foreach (var field in TargetFields)
            {
                if (obj.TryGetPropertyValue(field, out var value))
                {
                    item.Fields[field] = value?.ToJsonString()?.Trim('"');
                }
            }

            item.Type = item.Fields.GetValueOrDefault("Type") ?? obj["$type"]?.ToJsonString()?.Trim('"') ?? string.Empty;
            item.Text = item.Fields.GetValueOrDefault("Text");
            item.FilePath = item.Fields.GetValueOrDefault("FilePath");
            item.Frame = ParseInt(item.Fields.GetValueOrDefault("Frame"));
            item.Layer = ParseInt(item.Fields.GetValueOrDefault("Layer"));
            item.Length = ParseInt(item.Fields.GetValueOrDefault("Length"));

            if (item.Fields.Count > 0)
            {
                item.InternalId = internalItemIdService.BuildIdentity(item).InternalId;
                timeline.Items.Add(item);
            }

            foreach (var kv in obj)
            {
                Visit(kv.Value, $"{scope}.{kv.Key}", timeline);
            }

            return;
        }

        if (node is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                Visit(arr[i], $"{scope}[{i}]", timeline);
            }
        }
    }

    private static int ParseInt(string? value)
    {
        return int.TryParse(value, out var parsed) ? parsed : 0;
    }

    private static int TryExtractTimelineIndex(string scope)
    {
        var match = TimelineIndexRegex.Match(scope);
        return match.Success && int.TryParse(match.Groups[1].Value, out var index) ? index : -1;
    }
}

