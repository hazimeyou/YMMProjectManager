namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelineRcValidationSummary(
    string RcVersion,
    string RouteIdentity,
    DateTimeOffset GeneratedAtUtc,
    string BuildConfiguration,
    string CommitHash,
    bool BuildSucceeded,
    int WarningCount,
    int ErrorCount,
    bool FallbackPreserved,
    bool TimelineViewIntegrationFrozen,
    bool RuntimeBridgeFrozen,
    bool PreviewWorkspaceValidated,
    bool SnapshotBrowserValidated,
    bool SessionRestoreValidated,
    bool ValidationLoggingValidated,
    bool DiagnosticsExportValidated,
    bool CompareFlowValidated,
    bool DefaultDisabled,
    bool ExperimentalUntouched,
    bool ReleaseCandidateReady);
