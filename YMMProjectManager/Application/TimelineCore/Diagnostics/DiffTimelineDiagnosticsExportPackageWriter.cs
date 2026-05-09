using System.Text.Json;

namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineDiagnosticsExportPackageWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static DiffTimelineDiagnosticsExportPackageResult Export(
        string diagnosticsDirectory,
        DiffTimelineRouteValidationReport report,
        DiffTimelineValidationRunHistory history,
        DiffTimelineValidationDashboard dashboard,
        DiffTimelineStandaloneConfig config,
        DiffTimelinePreviewReadiness? previewReadiness = null,
        DiffTimelineFilteredResult? filteredResult = null,
        DiffTimelineSnapshotBrowserState? snapshotBrowserState = null,
        IReadOnlyList<DiffTimelineComparisonHistoryEntry>? comparisonHistory = null)
    {
        var warnings = new List<string>();
        var exported = new List<string>();
        try
        {
            Directory.CreateDirectory(diagnosticsDirectory);
            var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            var exportDir = Path.Combine(diagnosticsDirectory, $"difftimeline-export-{timestamp}");
            Directory.CreateDirectory(exportDir);

            CopyIfExists(report.DiagnosticsPath, exportDir, exported, warnings);

            var historyPath = Path.Combine(exportDir, "validation-history.json");
            File.WriteAllText(historyPath, JsonSerializer.Serialize(history, JsonOptions));
            exported.Add(historyPath);

            var reportPath = Path.Combine(exportDir, "route-validation-report.json");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions));
            exported.Add(reportPath);

            var dashboardPath = Path.Combine(exportDir, "validation-dashboard.json");
            File.WriteAllText(dashboardPath, JsonSerializer.Serialize(dashboard, JsonOptions));
            exported.Add(dashboardPath);

            var configPath = Path.Combine(exportDir, "standalone-config-snapshot.json");
            File.WriteAllText(configPath, JsonSerializer.Serialize(config.ToEnvironmentSnapshot(), JsonOptions));
            exported.Add(configPath);
            
            if (previewReadiness is not null)
            {
                var previewPath = Path.Combine(exportDir, "preview-readiness-report.json");
                File.WriteAllText(previewPath, JsonSerializer.Serialize(previewReadiness, JsonOptions));
                exported.Add(previewPath);
            }
            if (filteredResult is not null)
            {
                var filterPath = Path.Combine(exportDir, "filter-search-state.json");
                File.WriteAllText(filterPath, JsonSerializer.Serialize(filteredResult, JsonOptions));
                exported.Add(filterPath);
            }
            if (snapshotBrowserState is not null)
            {
                var browserPath = Path.Combine(exportDir, "snapshot-browser-state.json");
                File.WriteAllText(browserPath, JsonSerializer.Serialize(snapshotBrowserState, JsonOptions));
                exported.Add(browserPath);
            }
            if (comparisonHistory is not null)
            {
                var comparisonHistoryPath = Path.Combine(exportDir, "comparison-history.json");
                File.WriteAllText(comparisonHistoryPath, JsonSerializer.Serialize(comparisonHistory, JsonOptions));
                exported.Add(comparisonHistoryPath);
            }

            var manifest = new
            {
                createdAt = DateTimeOffset.Now,
                exportedFiles = exported,
                warnings,
            };
            var manifestPath = Path.Combine(exportDir, "manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
            exported.Add(manifestPath);

            return new DiffTimelineDiagnosticsExportPackageResult(true, exportDir, manifestPath, exported, warnings);
        }
        catch (Exception ex)
        {
            warnings.Add(ex.Message);
            return new DiffTimelineDiagnosticsExportPackageResult(false, diagnosticsDirectory, string.Empty, exported, warnings);
        }
    }

    private static void CopyIfExists(string path, string exportDir, List<string> exported, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            warnings.Add("diagnostics-path-empty");
            return;
        }

        if (!File.Exists(path))
        {
            warnings.Add("diagnostics-path-not-found");
            return;
        }

        var fileName = Path.GetFileName(path);
        var destination = Path.Combine(exportDir, fileName);
        File.Copy(path, destination, overwrite: true);
        exported.Add(destination);
    }
}
