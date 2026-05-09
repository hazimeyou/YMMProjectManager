namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelinePreviewPackageManifest(
    string Version,
    string CommitHash,
    IReadOnlyList<string> RequiredEnvironmentFlags,
    bool DefaultDisabled,
    bool FallbackPreserved,
    string ReadinessReportPath,
    string DiagnosticsExportPath,
    string RcValidationSummaryPath,
    IReadOnlyList<string> KnownLimitations);

public sealed record DiffTimelinePreviewValidationRunnerResult(
    bool Succeeded,
    DiffTimelineStandaloneSelfCheckResult SelfCheck,
    DiffTimelinePreviewReadiness PreviewReadiness,
    DiffTimelineDiagnosticsExportPackageResult ExportPackage,
    DiffTimelinePreviewPackageManifest Manifest,
    IReadOnlyList<string> FailureReasons,
    IReadOnlyList<string> Warnings);
