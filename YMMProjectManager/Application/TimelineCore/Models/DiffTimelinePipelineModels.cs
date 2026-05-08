namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelineStandalonePipelineDiagnostics(
    string OldProjectId,
    string NewProjectId,
    int OldTimelineCount,
    int NewTimelineCount,
    int OldLayerCount,
    int NewLayerCount,
    int OldItemCount,
    int NewItemCount,
    int AddedCount,
    int RemovedCount,
    int ChangedCount,
    int MovedCount,
    int RenamedCount,
    int PropertyChangedCount,
    int SemanticChangeCount,
    int RowCount,
    int GroupCount,
    long BuildDurationMilliseconds,
    string StageSummary,
    IReadOnlyDictionary<string, string> OptionsSnapshot,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record DiffTimelineStandalonePipelineResult(
    DiffTimelineCoreResult CoreResult,
    DiffTimelineSemanticDiffResult SemanticDiff,
    DiffTimelineStandalonePipelineDiagnostics Diagnostics);

public sealed record DiffTimelineStandalonePipelineOptions(
    DiffTimelineCoreBuildOptions? CoreBuildOptions = null,
    IReadOnlyDictionary<string, string>? OptionSnapshot = null);
