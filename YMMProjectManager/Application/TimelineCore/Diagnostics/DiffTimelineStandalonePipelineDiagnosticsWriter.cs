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
        string fallbackReason,
        DiffTimelineExistingRouteSummary? existingRouteSummary = null,
        DiffTimelineValidationComparerResult? comparerResult = null,
        DiffTimelineStandalonePromotionReadiness? promotionReadiness = null,
        DiffTimelineRouteSelectionResult? routeSelection = null,
        IReadOnlyDictionary<string, string>? environmentFlags = null,
        DiffTimelineRouteValidationReport? routeValidationReport = null)
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
                oldSnapshotHash = result.Diagnostics.Metadata.GetValueOrDefault("oldSnapshotHash"),
                newSnapshotHash = result.Diagnostics.Metadata.GetValueOrDefault("newSnapshotHash"),
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
            snapshotSource = result.Diagnostics.Metadata.GetValueOrDefault("snapshotSource"),
            adapterSource = result.Diagnostics.Metadata.GetValueOrDefault("adapterSource"),
            conversionResult = result.Diagnostics.Metadata.GetValueOrDefault("conversionResult"),
            skippedFields = result.Diagnostics.Metadata.GetValueOrDefault("skippedFields"),
            unsupportedFields = result.Diagnostics.Metadata.GetValueOrDefault("unsupportedFields"),
            fallbackReason,
            pipelineResultHash = result.Diagnostics.Metadata.GetValueOrDefault("pipelineResultHash"),
            elapsedTime = result.Diagnostics.BuildDurationMilliseconds,
            stageSummary = result.Diagnostics.StageSummary,
            optionsSnapshot = result.Diagnostics.OptionsSnapshot,
            metadata = result.Diagnostics.Metadata,
            existingRouteSummary,
            standaloneRouteSummary = new
            {
                result.Diagnostics.RowCount,
                result.Diagnostics.GroupCount,
                result.Diagnostics.AddedCount,
                result.Diagnostics.RemovedCount,
                result.Diagnostics.ChangedCount,
            },
            comparerResult,
            promotionReadiness,
            routeSelection,
            environmentFlags,
            routeValidationReport,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions));
        return path;
    }
}
