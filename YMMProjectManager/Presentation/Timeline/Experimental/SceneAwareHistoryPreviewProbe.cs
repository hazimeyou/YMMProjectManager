using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace YMMProjectManager.Presentation.Timeline.Experimental;

internal static class SceneAwareHistoryPreviewProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static SceneAwareHistoryPreviewProbeResult Run(string diagnosticsDirectory)
    {
        Directory.CreateDirectory(diagnosticsDirectory);
        var now = DateTimeOffset.Now;
        var windows = System.Windows.Application.Current?.Windows.OfType<Window>().ToList() ?? [];

        var windowReports = windows.Select(BuildWindowReport).ToList();
        var nonExcludedWindows = windowReports.Where(x => !x.Excluded).ToList();

        var timelineCandidates = new List<SceneAwareTimelineCandidate>();
        foreach (var wr in nonExcludedWindows)
        {
            var owner = windows.FirstOrDefault(x => x.GetType().FullName == wr.WindowType);
            if (owner is null)
            {
                continue;
            }

            ScanVisualTree(owner, wr, timelineCandidates);
        }

        var bestCandidate = timelineCandidates
            .Where(x => !x.Excluded)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        var confidence = ResolveConfidence(bestCandidate, timelineCandidates.Count);
        var sceneName = bestCandidate?.SceneName ?? "(unknown)";
        var sceneIndex = bestCandidate?.SceneIndex;
        var currentFrame = bestCandidate?.CurrentFrame;
        var itemCount = bestCandidate?.ItemCount;
        var layerCount = bestCandidate?.LayerCount;
        var selectedCount = bestCandidate?.SelectedCount;

        var comparisonHistoryPath = Path.Combine(diagnosticsDirectory, "difftimeline-comparison-history.json");
        var workspaceStatePath = Path.Combine(diagnosticsDirectory, "preview-workspace-state.json");
        var routeValidationPath = Path.Combine(diagnosticsDirectory, "route-validation-report.json");
        var manifestPath = Path.Combine(diagnosticsDirectory, "manifest.json");

        var fingerprint = BuildTimelineFingerprint(sceneName, itemCount, layerCount, currentFrame);

        var result = new SceneAwareHistoryPreviewProbeResult(
            Route: "RouteB",
            Investigation: "SceneAwareHistoryPreview",
            Step: "YmmTimelineCandidateScan",
            ProbedAt: now,
            DefaultDisabled: true,
            FallbackPreserved: true,
            RouteAPreserved: true,
            TimelineViewIntegrationFrozen: true,
            ProductionEmbedding: false,
            RuntimeMutation: false,
            InputInjection: false,
            CurrentSceneDetected: !string.IsNullOrWhiteSpace(bestCandidate?.SceneName) || bestCandidate?.SceneIndex is not null,
            SceneHistoryLinkFeasible: File.Exists(workspaceStatePath) || File.Exists(comparisonHistoryPath),
            Confidence: confidence,
            TimelineViewType: bestCandidate?.ElementType ?? "(not-found)",
            TimelineViewModelType: bestCandidate?.DataContextType ?? "(not-found)",
            OwnerWindowType: bestCandidate?.OwnerWindowType ?? "(none)",
            OwnerDataContextType: bestCandidate?.OwnerDataContextType ?? "(none)",
            SceneName: sceneName,
            SceneIndex: sceneIndex,
            TimelineItemCount: itemCount,
            LayerCount: layerCount,
            SelectedItemCount: selectedCount,
            CurrentFrame: currentFrame,
            TimelineFingerprint: fingerprint,
            SnapshotHistoryCandidates: BuildCandidates(comparisonHistoryPath, workspaceStatePath, routeValidationPath, manifestPath),
            ReflectionPropertyHints: bestCandidate?.PropertyHints ?? [],
            WindowScan: new SceneAwareWindowScanReport(
                TotalWindows: windowReports.Count,
                ExcludedWindows: windowReports.Count(x => x.Excluded),
                CandidateWindows: windowReports.Count(x => !x.Excluded)),
            Windows: windowReports,
            TimelineCandidates: timelineCandidates,
            BestYmmTimelineCandidate: bestCandidate is null
                ? new SceneAwareBestTimelineCandidate(false, 0, "None", "", "", "")
                : new SceneAwareBestTimelineCandidate(true, bestCandidate.Score, ResolveConfidence(bestCandidate, timelineCandidates.Count), bestCandidate.ElementType, bestCandidate.DataContextType, bestCandidate.OwnerWindowType));

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
            TimelineFingerprint: result.TimelineFingerprint,
            WindowScan: result.WindowScan,
            BestYmmTimelineCandidate: result.BestYmmTimelineCandidate);
        var summaryPath = Path.Combine(diagnosticsDirectory, $"scene-aware-history-preview-summary-{stamp}.json");
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, JsonOptions));

        var reportPath = Path.Combine(diagnosticsDirectory, "scene-aware-history-preview-report.md");
        File.WriteAllText(reportPath, BuildMarkdownReport(result, probePath, summaryPath));

        return result with { ProbePath = probePath, SummaryPath = summaryPath, ReportPath = reportPath };
    }

    private static SceneAwareWindowReport BuildWindowReport(Window window)
    {
        var windowType = window.GetType().FullName ?? window.GetType().Name;
        var (excluded, excludedReason) = ResolveWindowExclusion(windowType);
        return new SceneAwareWindowReport(
            WindowType: windowType,
            Title: window.Title,
            IsVisible: window.IsVisible,
            IsLoaded: window.IsLoaded,
            DataContextType: window.DataContext?.GetType().FullName ?? "(none)",
            OwnerType: window.Owner?.GetType().FullName ?? "(none)",
            ActualWidth: window.ActualWidth,
            ActualHeight: window.ActualHeight,
            CandidateReason: excluded ? "" : "non-ProjectDiff window",
            Excluded: excluded,
            ExcludedReason: excludedReason);
    }

    private static (bool Excluded, string Reason) ResolveWindowExclusion(string windowType)
    {
        if (windowType.Contains("ProjectDiffWindow", StringComparison.Ordinal))
        {
            return (true, "ProjectDiffWindow is RouteA standalone host");
        }

        if (windowType.Contains("SceneAwareHistoryPreviewInvestigationWindow", StringComparison.Ordinal))
        {
            return (true, "Investigation window must not be treated as YMM runtime window");
        }

        if (windowType.StartsWith("YMMProjectManager.", StringComparison.Ordinal))
        {
            return (true, "YMMProjectManager-owned helper/investigation window");
        }

        return (false, "");
    }

    private static void ScanVisualTree(DependencyObject root, SceneAwareWindowReport ownerWindow, List<SceneAwareTimelineCandidate> output)
    {
        var stack = new Stack<(DependencyObject Node, int Depth)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (node is FrameworkElement fe)
            {
                var candidate = TryBuildCandidate(fe, ownerWindow, depth);
                if (candidate is not null)
                {
                    output.Add(candidate);
                }
            }

            var children = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < children; i++)
            {
                stack.Push((VisualTreeHelper.GetChild(node, i), depth + 1));
            }
        }
    }

    private static SceneAwareTimelineCandidate? TryBuildCandidate(FrameworkElement element, SceneAwareWindowReport ownerWindow, int depth)
    {
        var elementType = element.GetType().FullName ?? element.GetType().Name;
        var typeName = element.GetType().Name;
        var dc = element.DataContext;
        var dcType = dc?.GetType().FullName ?? "(none)";

        var looksTimeline = typeName.Contains("TimelineView", StringComparison.OrdinalIgnoreCase)
            || elementType.Contains("Timeline", StringComparison.OrdinalIgnoreCase)
            || dcType.Contains("Timeline", StringComparison.OrdinalIgnoreCase);
        if (!looksTimeline)
        {
            return null;
        }

        var score = 0;
        if (typeName.Contains("TimelineView", StringComparison.OrdinalIgnoreCase)) score += 40;
        if (dcType.Contains("TimelineViewModel", StringComparison.OrdinalIgnoreCase)) score += 30;
        if (!ownerWindow.WindowType.StartsWith("YMMProjectManager.", StringComparison.Ordinal)) score += 20;
        if (HasTimelineLikeProps(dc)) score += 10;
        if (element.IsVisible && element.IsLoaded) score += 10;

        var excluded = false;
        var excludedReason = "";
        if (ownerWindow.WindowType.Contains("ProjectDiffWindow", StringComparison.Ordinal))
        {
            excluded = true;
            excludedReason = "RouteA standalone DiffTimeline, not YMM runtime TimelineView";
            score -= 100;
        }

        if (elementType.Contains("YMMProjectManager.Presentation.Views.DiffTimelineView", StringComparison.Ordinal)
            || dcType.Contains("YMMProjectManager.Presentation.ViewModels.DiffTimelineViewModel", StringComparison.Ordinal))
        {
            excluded = true;
            excludedReason = "RouteA standalone DiffTimeline, not YMM runtime TimelineView";
            score -= 100;
        }

        if (elementType.StartsWith("YMMProjectManager.", StringComparison.Ordinal))
        {
            score -= 50;
        }

        var sceneName = TryReadStringSafe(dc, out _, "SceneName", "CurrentScene", "CurrentSceneName", "ActiveSceneName");
        var sceneIndex = TryReadIntSafe(dc, out _, "SceneIndex", "CurrentSceneIndex", "ActiveSceneIndex");
        var currentFrame = TryReadIntSafe(dc, out _, "CurrentFrame", "Frame", "Position");
        var itemCount = TryReadCollectionCountSafe(dc, out _, "Items", "TimelineItems", "VisibleItems");
        var layerCount = TryReadCollectionCountSafe(dc, out _, "Layers", "VisibleLayers", "LayerItems");
        var selectedCount = TryReadCollectionCountSafe(dc, out _, "SelectedItems", "Selection", "Selected");

        var propertyHints = BuildPropertyHints(dc?.GetType());
        var propReads = BuildPropertyReadLogs(dc);

        return new SceneAwareTimelineCandidate(
            Score: score,
            Confidence: score >= 80 && !excluded ? "High" : score >= 50 && !excluded ? "Medium" : "Low",
            ElementType: elementType,
            ElementName: element.Name,
            DataContextType: dcType,
            OwnerWindowType: ownerWindow.WindowType,
            OwnerDataContextType: ownerWindow.DataContextType,
            VisualTreeDepth: depth,
            ActualWidth: element.ActualWidth,
            ActualHeight: element.ActualHeight,
            IsVisible: element.IsVisible,
            IsLoaded: element.IsLoaded,
            CandidateKind: typeName.Contains("TimelineView", StringComparison.OrdinalIgnoreCase) ? "TimelineViewType" : "TimelineLike",
            Excluded: excluded,
            ExcludedReason: excludedReason,
            SceneName: sceneName,
            SceneIndex: sceneIndex,
            ItemCount: itemCount,
            LayerCount: layerCount,
            SelectedCount: selectedCount,
            CurrentFrame: currentFrame,
            PropertyHints: propertyHints,
            PropertyReads: propReads);
    }

    private static bool HasTimelineLikeProps(object? source)
    {
        var t = source?.GetType();
        if (t is null) return false;
        return t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(x => x.Name.Contains("Timeline", StringComparison.OrdinalIgnoreCase)
                || x.Name.Contains("Scene", StringComparison.OrdinalIgnoreCase)
                || x.Name.Contains("Frame", StringComparison.OrdinalIgnoreCase));
    }

    private static List<SceneAwarePropertyReadLog> BuildPropertyReadLogs(object? source)
    {
        var logs = new List<SceneAwarePropertyReadLog>();
        if (source is null)
        {
            return logs;
        }

        var names = new[]
        {
            "Scene","CurrentScene","SelectedScene","SceneName","SceneIndex","Timeline","TimelineItems","Items","Layers","CurrentFrame","Frame","Position","Selection","SelectedItems"
        };

        foreach (var n in names)
        {
            var p = source.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
            if (p is null || p.GetMethod is null || p.GetMethod.GetParameters().Length != 0)
            {
                continue;
            }

            try
            {
                var v = p.GetValue(source);
                var preview = v switch
                {
                    null => "null",
                    string s => s,
                    int i => i.ToString(),
                    long l => l.ToString(),
                    bool b => b.ToString(),
                    Enum e => e.ToString(),
                    System.Collections.ICollection c => $"count={c.Count}",
                    _ => v.GetType().Name,
                };
                logs.Add(new SceneAwarePropertyReadLog(n, true, "", "", preview));
            }
            catch (Exception ex)
            {
                logs.Add(new SceneAwarePropertyReadLog(n, false, ex.GetType().FullName ?? ex.GetType().Name, ex.Message, ""));
            }
        }

        return logs;
    }

    private static string ResolveConfidence(SceneAwareTimelineCandidate? best, int total)
    {
        if (best is null)
        {
            return total == 0 ? "None" : "Low";
        }

        if (best.Excluded)
        {
            return "Low";
        }

        return best.Score >= 80 ? "High" : best.Score >= 50 ? "Medium" : "Low";
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
# Scene-aware History Preview Investigation Report (Step 2)

- route: {r.Route}
- investigation: {r.Investigation}
- step: {r.Step}
- probedAt: {r.ProbedAt:O}

## Safety
- defaultDisabled: {r.DefaultDisabled}
- fallbackPreserved: {r.FallbackPreserved}
- routeAPreserved: {r.RouteAPreserved}
- runtimeMutation: {r.RuntimeMutation}
- inputInjection: {r.InputInjection}
- productionEmbedding: {r.ProductionEmbedding}

## Window Scan
- totalWindows: {r.WindowScan.TotalWindows}
- excludedWindows: {r.WindowScan.ExcludedWindows}
- candidateWindows: {r.WindowScan.CandidateWindows}

## Best Candidate
- found: {r.BestYmmTimelineCandidate.Found}
- score: {r.BestYmmTimelineCandidate.Score}
- confidence: {r.BestYmmTimelineCandidate.Confidence}
- elementType: {r.BestYmmTimelineCandidate.ElementType}
- dataContextType: {r.BestYmmTimelineCandidate.DataContextType}
- ownerWindowType: {r.BestYmmTimelineCandidate.OwnerWindowType}

## Scene Snapshot
- currentSceneDetected: {r.CurrentSceneDetected}
- sceneName: {r.SceneName}
- sceneIndex: {r.SceneIndex?.ToString() ?? "(unknown)"}
- timelineItemCount: {r.TimelineItemCount?.ToString() ?? "(unknown)"}
- layerCount: {r.LayerCount?.ToString() ?? "(unknown)"}
- selectedItemCount: {r.SelectedItemCount?.ToString() ?? "(unknown)"}
- currentFrame: {r.CurrentFrame?.ToString() ?? "(unknown)"}
- timelineFingerprint: {r.TimelineFingerprint}

## Output Files
- probe: {probePath}
- summary: {summaryPath}
""";
    }

    private static string? TryReadStringSafe(object? source, out Exception? error, params string[] names)
    {
        error = null;
        foreach (var name in names)
        {
            var p = source?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p?.PropertyType != typeof(string) || p.GetMethod is null)
            {
                continue;
            }

            try
            {
                var value = p.GetValue(source) as string;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }

        return null;
    }

    private static int? TryReadIntSafe(object? source, out Exception? error, params string[] names)
    {
        error = null;
        foreach (var name in names)
        {
            var p = source?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p?.GetMethod is null)
            {
                continue;
            }

            try
            {
                var value = p.GetValue(source);
                if (value is int i)
                {
                    return i;
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }

        return null;
    }

    private static int? TryReadCollectionCountSafe(object? source, out Exception? error, params string[] names)
    {
        error = null;
        foreach (var name in names)
        {
            var p = source?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p?.GetMethod is null)
            {
                continue;
            }

            try
            {
                if (p.GetValue(source) is System.Collections.ICollection c)
                {
                    return c.Count;
                }
            }
            catch (Exception ex)
            {
                error = ex;
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
    string TimelineFingerprint,
    SceneAwareWindowScanReport WindowScan,
    SceneAwareBestTimelineCandidate BestYmmTimelineCandidate);

internal sealed record SceneAwareHistoryPreviewProbeResult(
    string Route,
    string Investigation,
    string Step,
    DateTimeOffset ProbedAt,
    bool DefaultDisabled,
    bool FallbackPreserved,
    bool RouteAPreserved,
    bool TimelineViewIntegrationFrozen,
    bool ProductionEmbedding,
    bool RuntimeMutation,
    bool InputInjection,
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
    SceneAwareWindowScanReport WindowScan,
    IReadOnlyList<SceneAwareWindowReport> Windows,
    IReadOnlyList<SceneAwareTimelineCandidate> TimelineCandidates,
    SceneAwareBestTimelineCandidate BestYmmTimelineCandidate,
    string ProbePath = "",
    string SummaryPath = "",
    string ReportPath = "");

internal sealed record SceneAwareWindowScanReport(int TotalWindows, int ExcludedWindows, int CandidateWindows);

internal sealed record SceneAwareWindowReport(
    string WindowType,
    string Title,
    bool IsVisible,
    bool IsLoaded,
    string DataContextType,
    string OwnerType,
    double ActualWidth,
    double ActualHeight,
    string CandidateReason,
    bool Excluded,
    string ExcludedReason);

internal sealed record SceneAwareTimelineCandidate(
    int Score,
    string Confidence,
    string ElementType,
    string ElementName,
    string DataContextType,
    string OwnerWindowType,
    string OwnerDataContextType,
    int VisualTreeDepth,
    double ActualWidth,
    double ActualHeight,
    bool IsVisible,
    bool IsLoaded,
    string CandidateKind,
    bool Excluded,
    string ExcludedReason,
    string? SceneName,
    int? SceneIndex,
    int? ItemCount,
    int? LayerCount,
    int? SelectedCount,
    int? CurrentFrame,
    IReadOnlyList<string> PropertyHints,
    IReadOnlyList<SceneAwarePropertyReadLog> PropertyReads);

internal sealed record SceneAwareBestTimelineCandidate(
    bool Found,
    int Score,
    string Confidence,
    string ElementType,
    string DataContextType,
    string OwnerWindowType);

internal sealed record SceneAwarePropertyReadLog(
    string Property,
    bool ReadSucceeded,
    string ErrorType,
    string ErrorMessage,
    string ValuePreview);
