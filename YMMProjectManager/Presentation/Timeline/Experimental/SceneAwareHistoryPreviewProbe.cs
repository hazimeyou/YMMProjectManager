using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace YMMProjectManager.Presentation.Timeline.Experimental;

internal static class SceneAwareHistoryPreviewProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static SceneAwareHistoryPreviewProbeResult Run(string diagnosticsDirectory)
    {
        Directory.CreateDirectory(diagnosticsDirectory);
        var now = DateTimeOffset.Now;
        var currentWindow = System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive)
            ?? System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault();

        var timelineView = FindTimelineView(currentWindow);
        var dataContext = timelineView?.DataContext;
        var dataContextType = dataContext?.GetType();
        var sceneName = TryReadString(dataContext, "SceneName", "CurrentSceneName", "ActiveSceneName");
        var sceneIndex = TryReadInt(dataContext, "SceneIndex", "CurrentSceneIndex", "ActiveSceneIndex");
        var currentFrame = TryReadInt(dataContext, "CurrentFrame", "Frame", "CursorFrame");
        var itemCount = TryReadCollectionCount(dataContext, "Items", "VisibleItems", "TimelineItems");
        var layerCount = TryReadCollectionCount(dataContext, "Layers", "VisibleLayers", "LayerItems");
        var selectedCount = TryReadCollectionCount(dataContext, "SelectedItems", "Selection", "Selected");

        var comparisonHistoryPath = Path.Combine(diagnosticsDirectory, "difftimeline-comparison-history.json");
        var workspaceStatePath = Path.Combine(diagnosticsDirectory, "preview-workspace-state.json");
        var routeValidationPath = Path.Combine(diagnosticsDirectory, "route-validation-report.json");
        var manifestPath = Path.Combine(diagnosticsDirectory, "manifest.json");

        var fingerprint = BuildTimelineFingerprint(sceneName, itemCount, layerCount, currentFrame);

        var result = new SceneAwareHistoryPreviewProbeResult(
            Route: "RouteB",
            Investigation: "SceneAwareHistoryPreview",
            ProbedAt: now,
            DefaultDisabled: true,
            FallbackPreserved: true,
            RouteAPreserved: true,
            TimelineViewIntegrationFrozen: true,
            ProductionEmbedding: false,
            RuntimeMutation: false,
            CurrentSceneDetected: !string.IsNullOrWhiteSpace(sceneName) || sceneIndex is not null,
            SceneHistoryLinkFeasible: File.Exists(workspaceStatePath) || File.Exists(comparisonHistoryPath),
            Confidence: "Low",
            TimelineViewType: timelineView?.GetType().FullName ?? "(not-found)",
            TimelineViewModelType: dataContextType?.FullName ?? "(not-found)",
            OwnerWindowType: currentWindow?.GetType().FullName ?? "(none)",
            OwnerDataContextType: currentWindow?.DataContext?.GetType().FullName ?? "(none)",
            SceneName: sceneName ?? "(unknown)",
            SceneIndex: sceneIndex,
            TimelineItemCount: itemCount,
            LayerCount: layerCount,
            SelectedItemCount: selectedCount,
            CurrentFrame: currentFrame,
            TimelineFingerprint: fingerprint,
            SnapshotHistoryCandidates: BuildCandidates(comparisonHistoryPath, workspaceStatePath, routeValidationPath, manifestPath),
            ReflectionPropertyHints: BuildPropertyHints(dataContextType));

        var stamp = now.ToString("yyyyMMdd-HHmmss");
        var probePath = Path.Combine(diagnosticsDirectory, $"scene-aware-history-preview-probe-{stamp}.json");
        File.WriteAllText(probePath, JsonSerializer.Serialize(result, JsonOptions));

        var summary = new SceneAwareHistoryPreviewSummary(
            Route: result.Route,
            Investigation: result.Investigation,
            ProbedAt: result.ProbedAt,
            CurrentSceneDetected: result.CurrentSceneDetected,
            SceneHistoryLinkFeasible: result.SceneHistoryLinkFeasible,
            Confidence: result.Confidence,
            TimelineViewType: result.TimelineViewType,
            TimelineViewModelType: result.TimelineViewModelType,
            TimelineFingerprint: result.TimelineFingerprint);
        var summaryPath = Path.Combine(diagnosticsDirectory, $"scene-aware-history-preview-summary-{stamp}.json");
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, JsonOptions));

        var reportPath = Path.Combine(diagnosticsDirectory, "scene-aware-history-preview-report.md");
        File.WriteAllText(reportPath, BuildMarkdownReport(result, probePath, summaryPath));

        return result with { ProbePath = probePath, SummaryPath = summaryPath, ReportPath = reportPath };
    }

    private static FrameworkElement? FindTimelineView(DependencyObject? root)
    {
        if (root is null)
        {
            return null;
        }

        if (root is FrameworkElement fe && fe.GetType().Name.Contains("TimelineView", StringComparison.OrdinalIgnoreCase))
        {
            return fe;
        }

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            var match = FindTimelineView(child);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static string[] BuildPropertyHints(Type? vmType)
    {
        if (vmType is null)
        {
            return [];
        }

        return vmType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.Name.Contains("Scene", StringComparison.OrdinalIgnoreCase) ||
                        x.Name.Contains("Timeline", StringComparison.OrdinalIgnoreCase) ||
                        x.Name.Contains("Layer", StringComparison.OrdinalIgnoreCase) ||
                        x.Name.Contains("Select", StringComparison.OrdinalIgnoreCase))
            .Select(x => $"{x.Name}:{x.PropertyType.Name}")
            .Take(80)
            .ToArray();
    }

    private static string[] BuildCandidates(params string[] paths)
    {
        return paths.Where(File.Exists).Select(Path.GetFileName).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()!;
    }

    private static string BuildTimelineFingerprint(string? sceneName, int? itemCount, int? layerCount, int? currentFrame)
    {
        return $"scene={sceneName ?? "unknown"}|items={itemCount?.ToString() ?? "?"}|layers={layerCount?.ToString() ?? "?"}|frame={currentFrame?.ToString() ?? "?"}";
    }

    private static string BuildMarkdownReport(SceneAwareHistoryPreviewProbeResult r, string probePath, string summaryPath)
    {
        return $"""
# Scene-aware History Preview Investigation Report

- route: {r.Route}
- investigation: {r.Investigation}
- probedAt: {r.ProbedAt:O}
- defaultDisabled: {r.DefaultDisabled}
- fallbackPreserved: {r.FallbackPreserved}
- routeAPreserved: {r.RouteAPreserved}
- timelineViewIntegrationFrozen: {r.TimelineViewIntegrationFrozen}
- productionEmbedding: {r.ProductionEmbedding}
- runtimeMutation: {r.RuntimeMutation}

## Detection
- currentSceneDetected: {r.CurrentSceneDetected}
- sceneHistoryLinkFeasible: {r.SceneHistoryLinkFeasible}
- confidence: {r.Confidence}

## TimelineView
- timelineViewType: {r.TimelineViewType}
- timelineViewModelType: {r.TimelineViewModelType}
- ownerWindowType: {r.OwnerWindowType}
- ownerDataContextType: {r.OwnerDataContextType}

## Scene Snapshot
- sceneName: {r.SceneName}
- sceneIndex: {r.SceneIndex?.ToString() ?? "(unknown)"}
- timelineItemCount: {r.TimelineItemCount?.ToString() ?? "(unknown)"}
- layerCount: {r.LayerCount?.ToString() ?? "(unknown)"}
- selectedItemCount: {r.SelectedItemCount?.ToString() ?? "(unknown)"}
- currentFrame: {r.CurrentFrame?.ToString() ?? "(unknown)"}
- timelineFingerprint: {r.TimelineFingerprint}

## Snapshot/History Candidates
{string.Join(Environment.NewLine, r.SnapshotHistoryCandidates.Select(x => $"- {x}"))}

## Property Hints
{string.Join(Environment.NewLine, r.ReflectionPropertyHints.Select(x => $"- {x}"))}

## Output Files
- probe: {probePath}
- summary: {summaryPath}
""";
    }

    private static string? TryReadString(object? source, params string[] names)
    {
        foreach (var name in names)
        {
            var p = source?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p?.PropertyType == typeof(string))
            {
                var value = p.GetValue(source) as string;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static int? TryReadInt(object? source, params string[] names)
    {
        foreach (var name in names)
        {
            var p = source?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p is null)
            {
                continue;
            }

            var value = p.GetValue(source);
            if (value is int i)
            {
                return i;
            }
        }

        return null;
    }

    private static int? TryReadCollectionCount(object? source, params string[] names)
    {
        foreach (var name in names)
        {
            var p = source?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p is null)
            {
                continue;
            }

            if (p.GetValue(source) is System.Collections.ICollection c)
            {
                return c.Count;
            }
        }

        return null;
    }
}

internal sealed record SceneAwareHistoryPreviewSummary(
    string Route,
    string Investigation,
    DateTimeOffset ProbedAt,
    bool CurrentSceneDetected,
    bool SceneHistoryLinkFeasible,
    string Confidence,
    string TimelineViewType,
    string TimelineViewModelType,
    string TimelineFingerprint);

internal sealed record SceneAwareHistoryPreviewProbeResult(
    string Route,
    string Investigation,
    DateTimeOffset ProbedAt,
    bool DefaultDisabled,
    bool FallbackPreserved,
    bool RouteAPreserved,
    bool TimelineViewIntegrationFrozen,
    bool ProductionEmbedding,
    bool RuntimeMutation,
    bool CurrentSceneDetected,
    bool SceneHistoryLinkFeasible,
    string Confidence,
    string TimelineViewType,
    string TimelineViewModelType,
    string OwnerWindowType,
    string OwnerDataContextType,
    string SceneName,
    int? SceneIndex,
    int? TimelineItemCount,
    int? LayerCount,
    int? SelectedItemCount,
    int? CurrentFrame,
    string TimelineFingerprint,
    IReadOnlyList<string> SnapshotHistoryCandidates,
    IReadOnlyList<string> ReflectionPropertyHints,
    string ProbePath = "",
    string SummaryPath = "",
    string ReportPath = "");
