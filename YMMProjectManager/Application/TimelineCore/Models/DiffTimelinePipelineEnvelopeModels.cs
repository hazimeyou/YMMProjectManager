namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelineStandalonePipelineEnvelope(
    DiffTimelineStandalonePipelineResult? Result,
    bool CacheHit,
    string SnapshotSource,
    string FallbackReason,
    bool IsSuccess,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record DiffTimelineStandaloneValidationStatus(
    bool Attempted,
    bool IsSuccess,
    bool CacheHit,
    string SnapshotSource,
    string FallbackReason,
    string StageSummary,
    string DiagnosticsPath,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);
