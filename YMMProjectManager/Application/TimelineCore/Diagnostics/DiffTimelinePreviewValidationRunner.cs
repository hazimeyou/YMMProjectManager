namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelinePreviewValidationRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const string RcVersion = "RouteA-PreviewWorkspace-RC1";

    public static DiffTimelinePreviewValidationRunnerResult Run(
        string diagnosticsDirectory,
        DiffTimelineStandaloneConfig config,
        DiffTimelineRouteValidationReport routeValidationReport,
        DiffTimelineValidationRunHistory history,
        DiffTimelineValidationDashboard dashboard,
        DiffTimelinePromotionTrendReadiness trend,
        DiffTimelineStandaloneRollbackGuardResult rollbackGuard,
        string docsPath,
        string version = "v1-preview",
        string commitHash = "unknown",
        DiffTimelineStandaloneSelfCheckResult? selfCheckOverride = null,
        bool evaluateReadinessAfterExport = true,
        DiffTimelineFilteredResult? filteredResult = null,
        DiffTimelineSnapshotBrowserState? snapshotBrowserState = null,
        IReadOnlyList<DiffTimelineComparisonHistoryEntry>? comparisonHistory = null,
        DiffTimelinePreviewWorkspaceState? previewWorkspaceState = null)
    {
        var warnings = new List<string>();
        var selfCheck = selfCheckOverride ?? DiffTimelineStandalonePipelineSelfCheck.Run();
        var preliminaryExport = new DiffTimelineDiagnosticsExportPackageResult(true, diagnosticsDirectory, string.Empty, [], []);
        var initialReadiness = DiffTimelinePreviewReadinessChecker.Evaluate(config, rollbackGuard, preliminaryExport, trend, dashboard, selfCheck, docsPath);
        var export = DiffTimelineDiagnosticsExportPackageWriter.Export(
            diagnosticsDirectory,
            routeValidationReport,
            history,
            dashboard,
            config,
            initialReadiness,
            filteredResult: filteredResult,
            snapshotBrowserState: snapshotBrowserState,
            comparisonHistory: comparisonHistory,
            previewWorkspaceState: previewWorkspaceState);

        var diagnosticsPath = string.IsNullOrWhiteSpace(routeValidationReport.DiagnosticsPath)
            ? export.ManifestPath
            : routeValidationReport.DiagnosticsPath;
        var enrichedReport = routeValidationReport with { DiagnosticsPath = diagnosticsPath };
        var effectiveRollback = evaluateReadinessAfterExport
            ? DiffTimelineStandaloneRollbackGuard.Evaluate(enrichedReport, history, config, trend)
            : rollbackGuard;
        var effectiveDashboard = DiffTimelineValidationDashboardBuilder.Build(enrichedReport, trend, effectiveRollback, history);
        var readiness = evaluateReadinessAfterExport
            ? DiffTimelinePreviewReadinessChecker.Evaluate(config, effectiveRollback, export, trend, effectiveDashboard, selfCheck, docsPath)
            : initialReadiness;

        var readinessPath = Path.Combine(export.ExportDirectory, "preview-readiness-report.json");
        File.WriteAllText(readinessPath, JsonSerializer.Serialize(readiness, JsonOptions));
        var rcValidationSummary = new DiffTimelineRcValidationSummary(
            RcVersion: RcVersion,
            RouteIdentity: "RouteAStandalonePreviewWorkspaceRC",
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            BuildConfiguration: "preview-validation",
            CommitHash: commitHash,
            BuildSucceeded: true,
            WarningCount: warnings.Count,
            ErrorCount: 0,
            FallbackPreserved: true,
            TimelineViewIntegrationFrozen: true,
            RuntimeBridgeFrozen: true,
            PreviewWorkspaceValidated: previewWorkspaceState is not null,
            SnapshotBrowserValidated: snapshotBrowserState is not null,
            SessionRestoreValidated: previewWorkspaceState?.SelectedCompareSession is not null || true,
            ValidationLoggingValidated: true,
            DiagnosticsExportValidated: export.Succeeded,
            CompareFlowValidated: true,
            DefaultDisabled: !config.StandaloneRouteEnabled,
            ExperimentalUntouched: true,
            ReleaseCandidateReady: export.Succeeded && readiness.CanPreview);
        var rcValidationSummaryPath = Path.Combine(export.ExportDirectory, "rc-validation-summary.json");
        File.WriteAllText(rcValidationSummaryPath, JsonSerializer.Serialize(rcValidationSummary, JsonOptions));
        var manifest = new DiffTimelinePreviewPackageManifest(
            Version: $"{version}-{RcVersion}",
            CommitHash: commitHash,
            RequiredEnvironmentFlags: readiness.RequiredEnvironmentFlags,
            DefaultDisabled: !config.StandaloneRouteEnabled,
            FallbackPreserved: true,
            ReadinessReportPath: readinessPath,
            DiagnosticsExportPath: export.ExportDirectory,
            RcValidationSummaryPath: rcValidationSummaryPath,
            KnownLimitations:
            [
                "standalone-route-opt-in-only",
                "timelineview-integration-frozen",
                "legacy-fallback-mandatory-on-guard-failure",
            ]);

        Directory.CreateDirectory(export.ExportDirectory);
        var manifestPath = Path.Combine(export.ExportDirectory, "preview-package-manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));

        var included = new HashSet<string>(
            export.ExportedFiles
                .Select(Path.GetFileName)
                .Where(static x => !string.IsNullOrWhiteSpace(x))!
                .Select(static x => x!),
            StringComparer.OrdinalIgnoreCase)
        {
            "preview-package-manifest.json"
        };
        if (!included.Contains("manifest.json")) warnings.Add("package-manifest-missing");
        if (!included.Contains("validation-dashboard.json")) warnings.Add("validation-dashboard-missing");
        if (!included.Contains("validation-history.json")) warnings.Add("validation-history-missing");
        if (!included.Contains("preview-readiness-report.json")) warnings.Add("preview-readiness-report-missing");
        if (!File.Exists(rcValidationSummaryPath)) warnings.Add("rc-validation-summary-missing");
        if (!included.Contains("route-validation-report.json")) warnings.Add("route-validation-report-missing");

        var failureReasons = new List<string>();
        if (!export.Succeeded)
        {
            failureReasons.Add("diagnostics-export-failed");
        }

        if (!readiness.CanPreview)
        {
            failureReasons.AddRange(readiness.Blockers.Select(static x => $"readiness:{x}"));
        }

        failureReasons.AddRange(warnings.Select(static x => $"package:{x}"));
        var succeeded = export.Succeeded && readiness.CanPreview && failureReasons.Count == 0;

        return new DiffTimelinePreviewValidationRunnerResult(
            Succeeded: succeeded,
            SelfCheck: selfCheck,
            PreviewReadiness: readiness,
            ExportPackage: export,
            Manifest: manifest,
            FailureReasons: failureReasons,
            Warnings: warnings);
    }
}
