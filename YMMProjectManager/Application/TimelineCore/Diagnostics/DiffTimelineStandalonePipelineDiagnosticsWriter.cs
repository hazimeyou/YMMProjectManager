using System.Text.Json;

namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineStandalonePipelineDiagnosticsWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string WriteToFile(
        string directory,
        DiffTimelineStandalonePipelineResult result,
        IReadOnlyDictionary<string, string> roundTrip,
        string fallbackReason)
    {
        Directory.CreateDirectory(directory);
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var fileName = $"difftimeline-standalone-pipeline-diagnostics-{timestamp}.json";
        var path = Path.Combine(directory, fileName);

        var payload = new
        {
            pipelineInputSummary = new
            {
                result.Diagnostics.OldProjectId,
                result.Diagnostics.NewProjectId,
                result.Diagnostics.OldTimelineCount,
                result.Diagnostics.NewTimelineCount,
                result.Diagnostics.OldLayerCount,
                result.Diagnostics.NewLayerCount,
                result.Diagnostics.OldItemCount,
                result.Diagnostics.NewItemCount,
            },
            semanticChangeSummary = new
            {
                result.Diagnostics.AddedCount,
                result.Diagnostics.RemovedCount,
                result.Diagnostics.ChangedCount,
                result.Diagnostics.MovedCount,
                result.Diagnostics.RenamedCount,
                result.Diagnostics.PropertyChangedCount,
                semanticChangeCount = result.Diagnostics.SemanticChangeCount,
            },
            rowGroupSummary = new
            {
                rowCount = result.Diagnostics.RowCount,
                groupCount = result.Diagnostics.GroupCount,
                result.CoreResult.Summary.SummaryText,
            },
            serializerRoundTripResult = roundTrip,
            fallbackReason,
            elapsedTime = result.Diagnostics.BuildDurationMilliseconds,
            stageSummary = result.Diagnostics.StageSummary,
            optionsSnapshot = result.Diagnostics.OptionsSnapshot,
            metadata = result.Diagnostics.Metadata,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions));
        return path;
    }
}
