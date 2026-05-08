using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineStandalonePipeline
{
    public static DiffTimelineStandalonePipelineResult BuildFromSnapshots(
        DiffTimelineProjectSnapshot oldSnapshot,
        DiffTimelineProjectSnapshot newSnapshot,
        DiffTimelineStandalonePipelineOptions? options = null)
    {
        options ??= new DiffTimelineStandalonePipelineOptions();

        var sw = Stopwatch.StartNew();
        var semanticInput = new DiffTimelineSemanticDiffInput(oldSnapshot, newSnapshot, options.OptionSnapshot ?? new Dictionary<string, string>());
        var semantic = DiffTimelineSnapshotDiffBuilder.BuildSemanticDiff(semanticInput);
        var coreEntries = DiffTimelineSnapshotDiffBuilder.BuildCoreDiffEntries(semantic);

        var coreOptions = options.CoreBuildOptions ?? new DiffTimelineCoreBuildOptions();
        var mergedSnapshot = MergeOptionSnapshot(coreOptions.OptionSnapshot, options.OptionSnapshot, semantic);
        coreOptions = coreOptions with { OptionSnapshot = mergedSnapshot };

        var coreResult = DiffTimelineCoreBuilder.BuildResult(coreEntries, coreOptions);
        sw.Stop();

        var diagnostics = BuildDiagnostics(oldSnapshot, newSnapshot, semantic, coreResult, sw.ElapsedMilliseconds, mergedSnapshot);
        return new DiffTimelineStandalonePipelineResult(coreResult, semantic, diagnostics);
    }

    private static DiffTimelineStandalonePipelineDiagnostics BuildDiagnostics(
        DiffTimelineProjectSnapshot oldSnapshot,
        DiffTimelineProjectSnapshot newSnapshot,
        DiffTimelineSemanticDiffResult semantic,
        DiffTimelineCoreResult core,
        long durationMs,
        IReadOnlyDictionary<string, string> optionSnapshot)
    {
        var oldLayerCount = oldSnapshot.Timelines.Sum(t => t.Layers.Count);
        var newLayerCount = newSnapshot.Timelines.Sum(t => t.Layers.Count);
        var oldItemCount = oldSnapshot.Timelines.SelectMany(t => t.Layers).Sum(l => l.Items.Count);
        var newItemCount = newSnapshot.Timelines.SelectMany(t => t.Layers).Sum(l => l.Items.Count);

        return new DiffTimelineStandalonePipelineDiagnostics(
            OldProjectId: oldSnapshot.ProjectId,
            NewProjectId: newSnapshot.ProjectId,
            OldTimelineCount: oldSnapshot.Timelines.Count,
            NewTimelineCount: newSnapshot.Timelines.Count,
            OldLayerCount: oldLayerCount,
            NewLayerCount: newLayerCount,
            OldItemCount: oldItemCount,
            NewItemCount: newItemCount,
            AddedCount: semantic.AddedCount,
            RemovedCount: semantic.RemovedCount,
            ChangedCount: semantic.ChangedCount,
            MovedCount: semantic.MovedCount,
            RenamedCount: semantic.RenamedCount,
            PropertyChangedCount: semantic.PropertyChangedCount,
            SemanticChangeCount: semantic.Changes.Count,
            RowCount: core.RowSet.Rows.Count,
            GroupCount: core.Groups.Count,
            BuildDurationMilliseconds: durationMs,
            StageSummary: $"semantic:{semantic.SummaryText} | core:{core.Summary.SummaryText}",
            OptionsSnapshot: optionSnapshot,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stage"] = "snapshot->semantic->core->rows",
                ["schemaVersion"] = newSnapshot.Metadata.SchemaVersion,
                ["snapshotSource"] = $"{oldSnapshot.Metadata.SourceKind}->{newSnapshot.Metadata.SourceKind}",
                ["adapterSource"] = "pipeline-input",
                ["conversionResult"] = "success",
                ["skippedFields"] = "none",
                ["unsupportedFields"] = "none",
                ["oldSnapshotHash"] = oldSnapshot.Metadata.SnapshotHash,
                ["newSnapshotHash"] = newSnapshot.Metadata.SnapshotHash,
                ["pipelineResultHash"] = ComputePipelineHash(core, semantic),
            });
    }

    private static IReadOnlyDictionary<string, string> MergeOptionSnapshot(
        IReadOnlyDictionary<string, string>? coreSnapshot,
        IReadOnlyDictionary<string, string>? pipelineSnapshot,
        DiffTimelineSemanticDiffResult semantic)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (coreSnapshot is not null)
        {
            foreach (var kv in coreSnapshot) merged[kv.Key] = kv.Value;
        }

        if (pipelineSnapshot is not null)
        {
            foreach (var kv in pipelineSnapshot) merged[kv.Key] = kv.Value;
        }

        merged["pipeline"] = "DiffTimelineStandalonePipeline";
        merged["semanticSummary"] = semantic.SummaryText;
        return merged;
    }

    private static string ComputePipelineHash(DiffTimelineCoreResult core, DiffTimelineSemanticDiffResult semantic)
    {
        var source = $"{semantic.Changes.Count}|{core.RowSet.Rows.Count}|{core.Summary.SummaryText}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(bytes);
    }
}
