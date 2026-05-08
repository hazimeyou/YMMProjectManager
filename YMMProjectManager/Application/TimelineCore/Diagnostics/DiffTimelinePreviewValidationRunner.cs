using System.Text.Json;

namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelinePreviewValidationRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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
        string commitHash = "unknown")
    {
        var warnings = new List<string>();
        var selfCheck = DiffTimelineStandalonePipelineSelfCheck.Run();
        var preliminaryExport = new DiffTimelineDiagnosticsExportPackageResult(true, diagnosticsDirectory, string.Empty, [], []);
        var readiness = DiffTimelinePreviewReadinessChecker.Evaluate(config, rollbackGuard, preliminaryExport, trend, dashboard, selfCheck, docsPath);
        var export = DiffTimelineDiagnosticsExportPackageWriter.Export(
            diagnosticsDirectory,
            routeValidationReport,
            history,
            dashboard,
            config,
            readiness);

        var readinessPath = Path.Combine(export.ExportDirectory, "preview-readiness-report.json");
        var manifest = new DiffTimelinePreviewPackageManifest(
            Version: version,
            CommitHash: commitHash,
            RequiredEnvironmentFlags: readiness.RequiredEnvironmentFlags,
            DefaultDisabled: !config.StandaloneRouteEnabled,
            FallbackPreserved: true,
            ReadinessReportPath: readinessPath,
            DiagnosticsExportPath: export.ExportDirectory,
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
