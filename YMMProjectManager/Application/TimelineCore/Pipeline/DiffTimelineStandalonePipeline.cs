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
        return BuildEnvelopeFromSnapshots(oldSnapshot, newSnapshot, options).Result
            ?? throw new InvalidOperationException("Pipeline result is null.");
    }

    public static DiffTimelineStandalonePipelineEnvelope BuildEnvelopeFromSnapshots(
        DiffTimelineProjectSnapshot oldSnapshot,
        DiffTimelineProjectSnapshot newSnapshot,
        DiffTimelineStandalonePipelineOptions? options = null)
    {
        options ??= new DiffTimelineStandalonePipelineOptions();
        var warnings = new List<string>();
        var errors = new List<string>();

        try
        {
            var key = DiffTimelineSnapshotCacheKeyFactory.Create(oldSnapshot, newSnapshot, options.OptionSnapshot);
            if (options.SnapshotCache is not null && options.SnapshotCache.TryGet(key, out var cached) && cached is not null)
            {
                var cachedDiagnostics = cached.Diagnostics with
                {
                    Metadata = new Dictionary<string, string>(cached.Diagnostics.Metadata, StringComparer.Ordinal)
                    {
                        ["cacheHit"] = "true",
                        ["cacheKey"] = key.Value,
                    },
                };
                var cachedResult = cached with { Diagnostics = cachedDiagnostics };
                return new DiffTimelineStandalonePipelineEnvelope(cachedResult, true, ResolveSnapshotSource(oldSnapshot, newSnapshot), "none", true, errors, warnings);
            }

            var sw = Stopwatch.StartNew();
            var semanticInput = new DiffTimelineSemanticDiffInput(oldSnapshot, newSnapshot, options.OptionSnapshot ?? new Dictionary<string, string>());
            var semantic = DiffTimelineSnapshotDiffBuilder.BuildSemanticDiff(semanticInput);
            var coreEntries = DiffTimelineSnapshotDiffBuilder.BuildCoreDiffEntries(semantic);

            var coreOptions = options.CoreBuildOptions ?? new DiffTimelineCoreBuildOptions();
            var mergedSnapshot = MergeOptionSnapshot(coreOptions.OptionSnapshot, options.OptionSnapshot, semantic);
            coreOptions = coreOptions with { OptionSnapshot = mergedSnapshot };

            var coreResult = DiffTimelineCoreBuilder.BuildResult(coreEntries, coreOptions);
            sw.Stop();

            var diagnostics = BuildDiagnostics(oldSnapshot, newSnapshot, semantic, coreResult, sw.ElapsedMilliseconds, mergedSnapshot, false, key.Value);
            var result = new DiffTimelineStandalonePipelineResult(coreResult, semantic, diagnostics);
            options.SnapshotCache?.Set(key, result);
            return new DiffTimelineStandalonePipelineEnvelope(result, false, ResolveSnapshotSource(oldSnapshot, newSnapshot), "none", true, errors, warnings);
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            return new DiffTimelineStandalonePipelineEnvelope(null, false, ResolveSnapshotSource(oldSnapshot, newSnapshot), "pipeline-exception", false, errors, warnings);
        }
    }

    private static string ResolveSnapshotSource(DiffTimelineProjectSnapshot oldSnapshot, DiffTimelineProjectSnapshot newSnapshot)
    {
        return $"{oldSnapshot.Metadata.SourceKind}->{newSnapshot.Metadata.SourceKind}";
    }

    private static DiffTimelineStandalonePipelineDiagnostics BuildDiagnostics(
        DiffTimelineProjectSnapshot oldSnapshot,
        DiffTimelineProjectSnapshot newSnapshot,
        DiffTimelineSemanticDiffResult semantic,
        DiffTimelineCoreResult core,
        long durationMs,
        IReadOnlyDictionary<string, string> optionSnapshot,
        bool cacheHit,
        string cacheKey)
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
                ["snapshotSource"] = ResolveSnapshotSource(oldSnapshot, newSnapshot),
                ["adapterSource"] = "pipeline-input",
                ["conversionResult"] = "success",
                ["skippedFields"] = "none",
                ["unsupportedFields"] = "none",
                ["oldSnapshotHash"] = oldSnapshot.Metadata.SnapshotHash,
                ["newSnapshotHash"] = newSnapshot.Metadata.SnapshotHash,
                ["pipelineResultHash"] = ComputePipelineHash(core, semantic),
                ["cacheHit"] = cacheHit.ToString(),
                ["cacheKey"] = cacheKey,
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
