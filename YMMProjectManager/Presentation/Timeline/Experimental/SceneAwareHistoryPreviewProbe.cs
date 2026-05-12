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

        var scannedObjects = BuildScannedObjects(bestCandidate, windows);
        var allProperties = scannedObjects.SelectMany(x => x.Properties).ToList();
        var getterErrors = allProperties.Where(x => x.ReadAttempted && !x.ReadSucceeded).ToList();
        var sceneCandidates = allProperties.Where(x => x.Category == "SceneCandidate" && x.ReadSucceeded).ToList();
        var collectionCandidates = allProperties.Where(x => x.Category is "ItemCollectionCandidate" or "LayerCandidate" or "SelectionCandidate").ToList();
        var frameCandidates = allProperties.Where(x => x.Category == "FrameCandidate").ToList();
        var selectionCandidates = allProperties.Where(x => x.Category == "SelectionCandidate").ToList();
        var bestSceneCandidate = ResolveBestSceneCandidate(sceneCandidates);
        var bestTimelineCollectionCandidate = ResolveBestCollectionCandidate(collectionCandidates);

        var timelineFingerprint = BuildTimelineFingerprintCandidate(bestTimelineCollectionCandidate, getterErrors.Count);
        var sceneIdentityCandidate = BuildSceneIdentityCandidate(bestSceneCandidate, bestCandidate, timelineFingerprint);
        var historySources = ScanHistorySources(diagnosticsDirectory);
        var historyMatchCandidates = BuildHistoryMatchCandidates(historySources, sceneIdentityCandidate, timelineFingerprint);
        var bestHistoryMatchCandidate = historyMatchCandidates.OrderByDescending(x => x.Score).FirstOrDefault();
        var historyMatching = new SceneAwareHistoryMatchingSummary(
            SourceCount: historySources.Count,
            ReadSucceededCount: historySources.Count(x => x.ReadSucceeded),
            MetadataCandidateCount: historyMatchCandidates.Count,
            MatchCandidateCount: historyMatchCandidates.Count(x => x.Score > 0),
            BestMatchScore: bestHistoryMatchCandidate?.Score ?? 0,
            BestMatchConfidence: bestHistoryMatchCandidate?.Confidence ?? "None",
            HistoryLinkFeasible: historyMatchCandidates.Any(x => x.Confidence is "High" or "Medium"));
        var historyPreviewItems = BuildHistoryPreviewItems(historyMatchCandidates, bestHistoryMatchCandidate);
        var historyPreview = new SceneAwareHistoryPreviewSummaryMetrics(
            PreviewItemCount: historyPreviewItems.Count,
            BestPreviewItemScore: historyPreviewItems.FirstOrDefault()?.Score ?? 0,
            BestPreviewItemConfidence: historyPreviewItems.FirstOrDefault()?.Confidence ?? "None",
            HasHighConfidenceMatch: historyPreviewItems.Any(x => x.Confidence is "High"),
            RouteADetailHandoffPrepared: historyPreviewItems.Count > 0);
        var defaultRouteAHandoff = BuildRouteADetailHandoffCandidate(
            historyPreviewItems.FirstOrDefault(),
            sceneIdentityCandidate.TimelineFingerprintHash);
        var routeAHandoffGap = BuildRouteAHandoffGap(defaultRouteAHandoff);
        var confidence = ResolveConfidence(bestCandidate, timelineCandidates.Count);
        var sceneName = bestCandidate?.SceneName ?? "(unknown)";
        var sceneIndex = bestCandidate?.SceneIndex;
        var currentFrame = bestCandidate?.CurrentFrame;
        var itemCount = bestTimelineCollectionCandidate.Found ? bestTimelineCollectionCandidate.Count : bestCandidate?.ItemCount;
        var layerCount = bestCandidate?.LayerCount;
        var selectedCount = bestCandidate?.SelectedCount;

        var comparisonHistoryPath = Path.Combine(diagnosticsDirectory, "difftimeline-comparison-history.json");
        var workspaceStatePath = Path.Combine(diagnosticsDirectory, "preview-workspace-state.json");
        var routeValidationPath = Path.Combine(diagnosticsDirectory, "route-validation-report.json");
        var manifestPath = Path.Combine(diagnosticsDirectory, "manifest.json");

        var fingerprint = timelineFingerprint.StableHash.Length == 0
            ? BuildTimelineFingerprint(sceneName, itemCount, layerCount, currentFrame)
            : timelineFingerprint.StableHash;

        var result = new SceneAwareHistoryPreviewProbeResult(
            Route: "RouteB",
            Investigation: "SceneAwareHistoryPreview",
            Step: "SceneAwareHistoryListPreview",
            ProbedAt: now,
            DefaultDisabled: true,
            FallbackPreserved: true,
            RouteAPreserved: true,
            TimelineViewIntegrationFrozen: true,
            ProductionEmbedding: false,
            RuntimeMutation: false,
            InputInjection: false,
            CurrentSceneDetected: bestSceneCandidate.Found || !string.IsNullOrWhiteSpace(bestCandidate?.SceneName) || bestCandidate?.SceneIndex is not null,
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
                : new SceneAwareBestTimelineCandidate(true, bestCandidate.Score, ResolveConfidence(bestCandidate, timelineCandidates.Count), bestCandidate.ElementType, bestCandidate.DataContextType, bestCandidate.OwnerWindowType),
            SurfaceInventory: new SceneAwareSurfaceInventorySummary(
                ScannedObjectCount: scannedObjects.Count,
                PropertyCount: allProperties.Count,
                ReadablePropertyCount: allProperties.Count(x => x.CanRead && x.GetterIsPublic),
                SceneCandidateCount: sceneCandidates.Count,
                CollectionCandidateCount: collectionCandidates.Count,
                FrameCandidateCount: frameCandidates.Count,
                SelectionCandidateCount: selectionCandidates.Count,
                GetterErrorCount: getterErrors.Count),
            ScannedObjects: scannedObjects,
            BestSceneCandidate: bestSceneCandidate,
            BestTimelineCollectionCandidate: bestTimelineCollectionCandidate,
            GetterErrors: getterErrors,
            TimelineFingerprintDetails: timelineFingerprint,
            SceneIdentityCandidate: sceneIdentityCandidate,
            FingerprintSafety: new SceneAwareFingerprintSafety(
                MaxItemsScanned: 200,
                ActualItemsScanned: timelineFingerprint.ItemCount,
                GetterErrorCount: getterErrors.Count,
                PathExcludedFromHash: true,
                TextBodyExcludedFromHash: true,
                ReadOnly: true),
            HistoryMatching: historyMatching,
            HistorySources: historySources,
            HistoryMatchCandidates: historyMatchCandidates,
            HistoryPreview: historyPreview,
            HistoryPreviewItems: historyPreviewItems,
            RouteADetailHandoff: defaultRouteAHandoff,
            RouteADetailHandoffGap: routeAHandoffGap,
            BestHistoryMatchCandidate: bestHistoryMatchCandidate);

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
            TimelineFingerprintDetails: result.TimelineFingerprintDetails,
            WindowScan: result.WindowScan,
            BestYmmTimelineCandidate: result.BestYmmTimelineCandidate,
            SurfaceInventory: result.SurfaceInventory,
            BestSceneCandidate: result.BestSceneCandidate,
            BestTimelineCollectionCandidate: result.BestTimelineCollectionCandidate,
            TimelineFingerprint: result.TimelineFingerprint,
            SceneIdentityCandidate: result.SceneIdentityCandidate,
            HistoryMatching: result.HistoryMatching,
            HistoryPreview: result.HistoryPreview,
            HistoryPreviewItems: result.HistoryPreviewItems,
            RouteADetailHandoff: result.RouteADetailHandoff,
            RouteADetailHandoffGap: result.RouteADetailHandoffGap,
            BestHistoryMatchCandidate: result.BestHistoryMatchCandidate);
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

    private static List<SceneAwareScannedObjectReport> BuildScannedObjects(SceneAwareTimelineCandidate? best, List<Window> windows)
    {
        var objects = new List<SceneAwareScannedObjectReport>();
        if (best is null)
        {
            return objects;
        }

        var ownerWindow = windows.FirstOrDefault(x => string.Equals(x.GetType().FullName, best.OwnerWindowType, StringComparison.Ordinal));
        AddScannedObject(objects, "BestTimelineCandidate.DataContext", ownerWindow?.DataContext?.GetType().FullName == best.DataContextType ? ownerWindow?.DataContext : null);
        AddScannedObject(objects, "OwnerWindow.DataContext", ownerWindow?.DataContext);

        var mainWindow = System.Windows.Application.Current?.MainWindow;
        AddScannedObject(objects, "Application.MainWindow", mainWindow);
        AddScannedObject(objects, "Application.MainWindow.DataContext", mainWindow?.DataContext);

        return objects;
    }

    private static void AddScannedObject(List<SceneAwareScannedObjectReport> list, string sourceKind, object? source)
    {
        if (source is null)
        {
            list.Add(new SceneAwareScannedObjectReport(sourceKind, "(null)", []));
            return;
        }

        list.Add(new SceneAwareScannedObjectReport(
            sourceKind,
            source.GetType().FullName ?? source.GetType().Name,
            BuildSurfaceInventory(source)));
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

    private static List<SceneAwarePropertyReadResult> BuildSurfaceInventory(object source)
    {
        var props = source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var results = new List<SceneAwarePropertyReadResult>(props.Length);
        foreach (var p in props)
        {
            var category = ClassifyCategory(p.Name, p.PropertyType);
            var canRead = p.CanRead;
            var canWrite = p.CanWrite;
            var getterPublic = p.GetMethod?.IsPublic == true;
            var indexCount = p.GetIndexParameters().Length;
            var readAttempted = false;
            var readSucceeded = false;
            var errorType = "";
            var errorMessage = "";
            var valueKind = "none";
            var valueType = "";
            var valuePreview = "";
            int? collectionCount = null;
            var sampleTypes = new List<string>();
            var sampleValues = new List<string>();
            var frameLike = new List<string>();
            var layerLike = new List<string>();
            var textLike = new List<string>();
            var startLike = new List<string>();
            var endLike = new List<string>();
            var durationLike = new List<string>();

            if (canRead && getterPublic && indexCount == 0 && !typeof(System.Windows.Input.ICommand).IsAssignableFrom(p.PropertyType))
            {
                readAttempted = true;
                try
                {
                    var value = p.GetValue(source);
                    readSucceeded = true;
                    if (value is null)
                    {
                        valueKind = "null";
                        valuePreview = "null";
                    }
                    else
                    {
                        valueType = value.GetType().FullName ?? value.GetType().Name;
                        if (IsScalar(value.GetType()))
                        {
                            valueKind = "scalar";
                            valuePreview = SafeToString(value);
                        }
                        else if (value is System.Collections.ICollection collection)
                        {
                            valueKind = "collection";
                            collectionCount = collection.Count;
                            valuePreview = $"count={collection.Count}";
                            CollectSamples(collection, sampleTypes, sampleValues, frameLike, layerLike, textLike, startLike, endLike, durationLike);
                        }
                        else
                        {
                            valueKind = "object";
                            valuePreview = SafeToString(value);
                            CollectTypeHints(value.GetType(), frameLike, layerLike, textLike, startLike, endLike, durationLike);
                        }
                    }
                }
                catch (Exception ex)
                {
                    errorType = ex.GetType().FullName ?? ex.GetType().Name;
                    errorMessage = ex.Message;
                }
            }

            results.Add(new SceneAwarePropertyReadResult(
                p.Name,
                p.PropertyType.FullName ?? p.PropertyType.Name,
                canRead,
                canWrite,
                getterPublic,
                indexCount,
                p.DeclaringType?.FullName ?? "",
                category,
                readAttempted,
                readSucceeded,
                errorType,
                errorMessage,
                valueKind,
                valueType,
                valuePreview,
                collectionCount,
                sampleTypes,
                sampleValues,
                frameLike,
                layerLike,
                textLike,
                startLike,
                endLike,
                durationLike));
        }

        return results;
    }

    private static string[] BuildCandidates(params string[] paths)
    {
        return paths.Where(File.Exists).Select(Path.GetFileName).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()!;
    }

    private static bool IsScalar(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan) || t == typeof(Guid) || t == typeof(decimal);
    }

    private static string SafeToString(object value)
    {
        try
        {
            return value.ToString() ?? value.GetType().Name;
        }
        catch
        {
            return value.GetType().Name;
        }
    }

    private static string ClassifyCategory(string name, Type type)
    {
        if (name.Contains("Scene", StringComparison.OrdinalIgnoreCase)) return "SceneCandidate";
        if (name.Contains("Timeline", StringComparison.OrdinalIgnoreCase)) return "TimelineCandidate";
        if (name.Contains("Layer", StringComparison.OrdinalIgnoreCase)) return "LayerCandidate";
        if (name.Contains("Frame", StringComparison.OrdinalIgnoreCase) || name.Contains("Position", StringComparison.OrdinalIgnoreCase) || name.Contains("Cursor", StringComparison.OrdinalIgnoreCase)) return "FrameCandidate";
        if (name.Contains("Select", StringComparison.OrdinalIgnoreCase)) return "SelectionCandidate";
        if (name.Contains("Project", StringComparison.OrdinalIgnoreCase) || name.Contains("FilePath", StringComparison.OrdinalIgnoreCase)) return "ProjectCandidate";
        if (name.Contains("History", StringComparison.OrdinalIgnoreCase) || name.Contains("Undo", StringComparison.OrdinalIgnoreCase) || name.Contains("Redo", StringComparison.OrdinalIgnoreCase)) return "HistoryCandidate";
        if (typeof(System.Windows.Input.ICommand).IsAssignableFrom(type) || name.Contains("Command", StringComparison.OrdinalIgnoreCase)) return "CommandCandidate";
        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string)) return "ItemCollectionCandidate";
        return "Other";
    }

    private static void CollectSamples(System.Collections.ICollection collection, List<string> sampleTypes, List<string> sampleValues, List<string> frameLike, List<string> layerLike, List<string> textLike, List<string> startLike, List<string> endLike, List<string> durationLike)
    {
        var count = 0;
        foreach (var item in collection)
        {
            if (item is null)
            {
                continue;
            }

            sampleTypes.Add(item.GetType().FullName ?? item.GetType().Name);
            sampleValues.Add(SafeToString(item));
            CollectTypeHints(item.GetType(), frameLike, layerLike, textLike, startLike, endLike, durationLike);
            count++;
            if (count >= 3)
            {
                break;
            }
        }
    }

    private static void CollectTypeHints(Type type, List<string> frameLike, List<string> layerLike, List<string> textLike, List<string> startLike, List<string> endLike, List<string> durationLike)
    {
        foreach (var name in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(x => x.Name).Take(80))
        {
            if (name.Contains("Frame", StringComparison.OrdinalIgnoreCase)) frameLike.Add(name);
            if (name.Contains("Layer", StringComparison.OrdinalIgnoreCase)) layerLike.Add(name);
            if (name.Contains("Text", StringComparison.OrdinalIgnoreCase) || name.Contains("Name", StringComparison.OrdinalIgnoreCase) || name.Contains("FilePath", StringComparison.OrdinalIgnoreCase)) textLike.Add(name);
            if (name.Contains("Start", StringComparison.OrdinalIgnoreCase)) startLike.Add(name);
            if (name.Contains("End", StringComparison.OrdinalIgnoreCase)) endLike.Add(name);
            if (name.Contains("Duration", StringComparison.OrdinalIgnoreCase) || name.Contains("Length", StringComparison.OrdinalIgnoreCase)) durationLike.Add(name);
        }
    }

    private static string BuildTimelineFingerprint(string? sceneName, int? itemCount, int? layerCount, int? currentFrame)
    {
        return $"scene={sceneName ?? "unknown"}|items={itemCount?.ToString() ?? "?"}|layers={layerCount?.ToString() ?? "?"}|frame={currentFrame?.ToString() ?? "?"}";
    }

    private static string BuildStableText(
        int itemCount,
        int? layerCount,
        long? minFrame,
        long? maxFrame,
        long? totalDuration,
        IReadOnlyDictionary<string, int> itemTypeHistogram,
        IReadOnlyDictionary<string, int> layerHistogram,
        IReadOnlyDictionary<string, int> textPresenceHistogram)
    {
        var lines = new List<string>
        {
            $"itemCount={itemCount}",
            $"layerCount={layerCount?.ToString() ?? "null"}",
            $"minFrame={minFrame?.ToString() ?? "null"}",
            $"maxFrame={maxFrame?.ToString() ?? "null"}",
            $"totalDuration={totalDuration?.ToString() ?? "null"}",
            "types:"
        };
        foreach (var kv in itemTypeHistogram.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            lines.Add($"  {kv.Key}={kv.Value}");
        }

        lines.Add("layers:");
        foreach (var kv in layerHistogram.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            lines.Add($"  {kv.Key}={kv.Value}");
        }

        lines.Add("text:");
        foreach (var kv in textPresenceHistogram.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            lines.Add($"  {kv.Key}={kv.Value}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static List<SceneAwareHistorySource> ScanHistorySources(string diagnosticsDirectory)
    {
        var roots = new[]
        {
            diagnosticsDirectory,
            Path.Combine(Directory.GetCurrentDirectory(), "diagnostics"),
            Path.Combine(AppContext.BaseDirectory, "diagnostics"),
        }.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();

        var result = new List<SceneAwareHistorySource>();
        var patterns = new[] { "comparison-history.json", "preview-workspace-state.json", "*validation*.json", "*manifest*.json", "*summary*.json", "*snapshot*.json", "scene-aware-history-preview-probe-*.json" };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            foreach (var pattern in patterns)
            {
                foreach (var path in Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly))
                {
                    if (!seen.Add(path))
                    {
                        continue;
                    }

                    var fi = new FileInfo(path);
                    var readOk = false;
                    var errType = "";
                    var errMsg = "";
                    try
                    {
                        using var _ = File.OpenRead(path);
                        readOk = true;
                    }
                    catch (Exception ex)
                    {
                        errType = ex.GetType().FullName ?? ex.GetType().Name;
                        errMsg = ex.Message;
                    }

                    result.Add(new SceneAwareHistorySource(
                        SourcePath: path,
                        SourceFileName: fi.Name,
                        SourceKind: ResolveSourceKind(fi.Name),
                        Exists: fi.Exists,
                        ReadSucceeded: readOk,
                        ReadErrorType: errType,
                        ReadErrorMessage: errMsg,
                        ModifiedAt: fi.Exists ? fi.LastWriteTimeUtc : null,
                        SizeBytes: fi.Exists ? fi.Length : 0));
                }
            }
        }

        return result.OrderByDescending(x => x.ModifiedAt).ToList();
    }

    private static string ResolveSourceKind(string fileName)
    {
        var n = fileName.ToLowerInvariant();
        if (n.Contains("comparison-history")) return "ComparisonHistory";
        if (n.Contains("preview-workspace-state")) return "PreviewWorkspaceState";
        if (n.Contains("manifest")) return "Manifest";
        if (n.Contains("validation")) return "Validation";
        if (n.Contains("summary")) return "Summary";
        if (n.Contains("snapshot")) return "Snapshot";
        if (n.Contains("scene-aware-history-preview-probe")) return "SceneAwareProbe";
        return "Other";
    }

    private static string ComputeSha256(string text)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }

    private static List<SceneAwareHistoryMatchCandidate> BuildHistoryMatchCandidates(
        IReadOnlyList<SceneAwareHistorySource> sources,
        SceneAwareSceneIdentityCandidate identity,
        SceneAwareTimelineFingerprint fingerprint)
    {
        var list = new List<SceneAwareHistoryMatchCandidate>();
        foreach (var src in sources.Where(x => x.ReadSucceeded))
        {
            try
            {
                using var stream = File.OpenRead(src.SourcePath);
                using var doc = JsonDocument.Parse(stream);
                var flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                FlattenJson(doc.RootElement, flat, "", 0, 5, 2000);

                var sceneName = TryGetValue(flat, "sceneName", "SceneName");
                var sceneIndex = TryGetInt(flat, "sceneIndex", "SceneIndex");
                var stableHash = TryGetValue(flat, "stableHash", "StableHash", "timelineFingerprint.stableHash", "TimelineFingerprint.StableHash");
                var itemCount = TryGetInt(flat, "itemCount", "ItemCount");
                var layerCount = TryGetInt(flat, "layerCount", "LayerCount");
                var minFrame = TryGetLong(flat, "minFrame", "MinFrame");
                var maxFrame = TryGetLong(flat, "maxFrame", "MaxFrame");

                var reasons = new List<string>();
                var missing = new List<string>();
                var score = 0;
                if (!string.IsNullOrWhiteSpace(stableHash))
                {
                    if (string.Equals(stableHash, identity.TimelineFingerprintHash, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 100;
                        reasons.Add("stableHash exact match");
                    }
                    else
                    {
                        score -= 30;
                        reasons.Add("stableHash mismatch");
                    }
                }
                else
                {
                    missing.Add("stableHash");
                }

                if (!string.IsNullOrWhiteSpace(sceneName) && !string.Equals(sceneName, "<unknown>", StringComparison.OrdinalIgnoreCase) && string.Equals(sceneName, identity.SceneName, StringComparison.OrdinalIgnoreCase))
                {
                    score += 40;
                    reasons.Add("sceneName match");
                }
                else if (string.IsNullOrWhiteSpace(sceneName))
                {
                    missing.Add("sceneName");
                }

                if (sceneIndex is not null && identity.SceneIndex is not null && sceneIndex == identity.SceneIndex)
                {
                    score += 30;
                    reasons.Add("sceneIndex match");
                }
                else if (sceneIndex is null)
                {
                    missing.Add("sceneIndex");
                }

                if (itemCount is not null)
                {
                    if (itemCount == fingerprint.ItemCount)
                    {
                        score += 25;
                        reasons.Add("itemCount match");
                    }
                    else
                    {
                        score -= 30;
                        reasons.Add("itemCount mismatch");
                    }
                }
                else
                {
                    missing.Add("itemCount");
                }

                if (layerCount is not null && fingerprint.LayerCount is not null)
                {
                    if (layerCount == fingerprint.LayerCount)
                    {
                        score += 20;
                        reasons.Add("layerCount match");
                    }
                }

                if (minFrame is not null && maxFrame is not null && fingerprint.MinFrame is not null && fingerprint.MaxFrame is not null)
                {
                    if (minFrame == fingerprint.MinFrame && maxFrame == fingerprint.MaxFrame)
                    {
                        score += 20;
                        reasons.Add("frameRange match");
                    }
                }

                if (src.ModifiedAt is not null && src.ModifiedAt > DateTimeOffset.UtcNow.AddDays(-7))
                {
                    score += 5;
                    reasons.Add("recent file");
                }

                var confidence = score >= 80 ? "High" : score >= 40 ? "Medium" : score > 0 ? "Low" : "None";
                list.Add(new SceneAwareHistoryMatchCandidate(
                    SourceKind: src.SourceKind,
                    SourceFileName: src.SourceFileName,
                    SourcePath: src.SourcePath,
                    ModifiedAt: src.ModifiedAt,
                    SceneName: sceneName,
                    SceneIndex: sceneIndex,
                    StableHash: stableHash,
                    ItemCount: itemCount,
                    LayerCount: layerCount,
                    MinFrame: minFrame,
                    MaxFrame: maxFrame,
                    Score: score,
                    Confidence: confidence,
                    MatchReasons: reasons,
                    MissingFields: missing));
            }
            catch
            {
                // keep read-only investigation resilient
            }
        }

        return list.OrderByDescending(x => x.Score).ToList();
    }

    private static List<SceneAwareHistoryPreviewItem> BuildHistoryPreviewItems(
        IReadOnlyList<SceneAwareHistoryMatchCandidate> candidates,
        SceneAwareHistoryMatchCandidate? bestCandidate)
    {
        return candidates
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => ConfidenceRank(x.Confidence))
            .ThenByDescending(x => x.ModifiedAt ?? DateTimeOffset.MinValue)
            .ThenBy(x => x.SourceFileName, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Select((x, i) =>
            {
                var isBest = bestCandidate is not null
                    && string.Equals(x.SourcePath, bestCandidate.SourcePath, StringComparison.OrdinalIgnoreCase)
                    && x.Score == bestCandidate.Score;
                var reasons = x.MatchReasons.Count == 0 ? "(none)" : string.Join(", ", x.MatchReasons);
                var missing = x.MissingFields.Count == 0 ? "(none)" : string.Join(", ", x.MissingFields);
                return new SceneAwareHistoryPreviewItem(
                    Rank: i + 1,
                    Title: $"#{i + 1} {x.Confidence} score={x.Score} {x.SourceKind} / {x.SourceFileName}",
                    SourceKind: x.SourceKind,
                    SourceFileName: x.SourceFileName,
                    SourcePath: x.SourcePath,
                    ModifiedAt: x.ModifiedAt,
                    Score: x.Score,
                    Confidence: x.Confidence,
                    IsBestMatch: isBest,
                    SceneName: x.SceneName ?? "<unknown>",
                    SceneIndex: x.SceneIndex,
                    StableHash: x.StableHash ?? string.Empty,
                    ItemCount: x.ItemCount,
                    LayerCount: x.LayerCount,
                    MinFrame: x.MinFrame,
                    MaxFrame: x.MaxFrame,
                    MatchReasons: x.MatchReasons,
                    MissingFields: x.MissingFields,
                    SummaryText: $"#{i + 1} [{x.Confidence}] score={x.Score} {x.SourceKind} / {x.SourceFileName} | reasons: {reasons}",
                    DetailText: $"Missing: {missing}{Environment.NewLine}Modified: {(x.ModifiedAt?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "(unknown)")}{Environment.NewLine}Hash: {(string.IsNullOrWhiteSpace(x.StableHash) ? "(none)" : x.StableHash)}");
            })
            .ToList();
    }

    private static SceneAwareRouteADetailHandoffCandidate BuildRouteADetailHandoffCandidate(
        SceneAwareHistoryPreviewItem? selectedItem,
        string runtimeStableHash)
    {
        if (selectedItem is null)
        {
            return new SceneAwareRouteADetailHandoffCandidate(
                Prepared: false,
                CanOpen: false,
                Reason: "No selected history preview item",
                SourceKind: "",
                SourceFileName: "",
                SourcePath: "",
                SnapshotId: null,
                CompareSessionId: null,
                RouteValidationReportPath: null,
                PreviewWorkspaceStatePath: null,
                ComparisonHistoryPath: null,
                RuntimeStableHash: runtimeStableHash,
                HistoryStableHash: "",
                Score: 0,
                Confidence: "None",
                AvailableFields: [],
                MissingFields: ["sourcePath"],
                Warnings: ["Open RouteA Detail Diff is not enabled in Step 7A (dry-run only)."],
                SummaryText: "routeA handoff: not prepared");
        }

        var extracted = ExtractRouteAHandoffFields(selectedItem.SourcePath);
        var available = new List<string>();
        var missing = new List<string>();
        void Track(string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) missing.Add(name); else available.Add(name);
        }

        Track("snapshotId", extracted.SnapshotId);
        Track("compareSessionId", extracted.CompareSessionId);
        Track("routeValidationReportPath", extracted.RouteValidationReportPath);
        Track("previewWorkspaceStatePath", extracted.PreviewWorkspaceStatePath);
        Track("comparisonHistoryPath", extracted.ComparisonHistoryPath);

        var canOpen = !string.IsNullOrWhiteSpace(selectedItem.SourcePath)
            && (selectedItem.Confidence is "High" or "Medium")
            && (!string.IsNullOrWhiteSpace(extracted.CompareSessionId) || !string.IsNullOrWhiteSpace(extracted.SnapshotId));
        var reason = canOpen ? "RouteA handoff metadata is sufficient (dry-run only)" : "Insufficient RouteA handoff metadata";
        var warnings = new List<string>();
        warnings.AddRange(extracted.Warnings);
        warnings.Add("Open RouteA Detail Diff is not enabled in Step 7A (dry-run only).");

        return new SceneAwareRouteADetailHandoffCandidate(
            Prepared: true,
            CanOpen: canOpen,
            Reason: reason,
            SourceKind: selectedItem.SourceKind,
            SourceFileName: selectedItem.SourceFileName,
            SourcePath: selectedItem.SourcePath,
            SnapshotId: extracted.SnapshotId,
            CompareSessionId: extracted.CompareSessionId,
            RouteValidationReportPath: extracted.RouteValidationReportPath,
            PreviewWorkspaceStatePath: extracted.PreviewWorkspaceStatePath,
            ComparisonHistoryPath: extracted.ComparisonHistoryPath,
            RuntimeStableHash: runtimeStableHash,
            HistoryStableHash: selectedItem.StableHash,
            Score: selectedItem.Score,
            Confidence: selectedItem.Confidence,
            AvailableFields: available,
            MissingFields: missing,
            Warnings: warnings,
            SummaryText: $"prepared=True canOpen={canOpen} reason={reason}");
    }

    private static void FlattenJson(JsonElement element, Dictionary<string, string> output, string prefix, int depth, int maxDepth, int maxProperties)
    {
        if (depth > maxDepth || output.Count >= maxProperties)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in element.EnumerateObject())
            {
                var key = string.IsNullOrEmpty(prefix) ? p.Name : $"{prefix}.{p.Name}";
                FlattenJson(p.Value, output, key, depth + 1, maxDepth, maxProperties);
                if (output.Count >= maxProperties) return;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in element.EnumerateArray())
            {
                if (i >= 50) break;
                FlattenJson(item, output, $"{prefix}[{i}]", depth + 1, maxDepth, maxProperties);
                i++;
                if (output.Count >= maxProperties) return;
            }
        }
        else
        {
            output[prefix] = element.ToString();
        }
    }

    private static string? TryGetValue(Dictionary<string, string> map, params string[] keys)
    {
        foreach (var k in keys)
        {
            var match = map.FirstOrDefault(x => x.Key.EndsWith(k, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Value))
            {
                return match.Value;
            }
        }

        return null;
    }

    private static int? TryGetInt(Dictionary<string, string> map, params string[] keys)
    {
        var value = TryGetValue(map, keys);
        return int.TryParse(value, out var i) ? i : null;
    }

    private static long? TryGetLong(Dictionary<string, string> map, params string[] keys)
    {
        var value = TryGetValue(map, keys);
        return long.TryParse(value, out var i) ? i : null;
    }

    private static int ConfidenceRank(string confidence) => confidence switch
    {
        "High" => 3,
        "Medium" => 2,
        "Low" => 1,
        _ => 0,
    };

    private static SceneAwareRouteAExtractedFields ExtractRouteAHandoffFields(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return new SceneAwareRouteAExtractedFields(null, null, null, null, null, ["source file not found"]);
        }

        try
        {
            const long maxFileBytes = 5 * 1024 * 1024;
            var fi = new FileInfo(sourcePath);
            if (fi.Length > maxFileBytes)
            {
                return new SceneAwareRouteAExtractedFields(null, null, null, null, null, ["source file is larger than 5MB and was skipped"]);
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(sourcePath));
            var flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            FlattenJson(doc.RootElement, flat, "", 0, 5, 2000);
            var snapshotId = TryGetValue(flat, "snapshotId", "SnapshotId", "oldSnapshot", "newSnapshot", "baseSnapshot", "targetSnapshot");
            var compareSessionId = TryGetValue(flat, "compareSessionId", "CompareSessionId", "sessionId", "SessionId");
            var routeValidationReportPath = TryGetValue(flat, "routeValidationReportPath", "RouteValidationReportPath");
            var previewWorkspaceStatePath = TryGetValue(flat, "previewWorkspaceStatePath", "PreviewWorkspaceStatePath");
            var comparisonHistoryPath = TryGetValue(flat, "comparisonHistoryPath", "ComparisonHistoryPath");
            return new SceneAwareRouteAExtractedFields(
                snapshotId,
                compareSessionId,
                routeValidationReportPath,
                previewWorkspaceStatePath,
                comparisonHistoryPath,
                []);
        }
        catch (Exception ex)
        {
            return new SceneAwareRouteAExtractedFields(null, null, null, null, null, [$"extract failed: {ex.GetType().Name}"]);
        }
    }

    private static SceneAwareRouteADetailHandoffGap BuildRouteAHandoffGap(SceneAwareRouteADetailHandoffCandidate handoff)
    {
        var critical = new List<string>();
        var important = new List<string>();
        var optional = new List<string>();

        if (string.IsNullOrWhiteSpace(handoff.CompareSessionId)) critical.Add("compareSessionId");
        if (string.IsNullOrWhiteSpace(handoff.SnapshotId)) critical.Add("snapshotId or snapshot pair");
        if (string.IsNullOrWhiteSpace(handoff.SourcePath) || !File.Exists(handoff.SourcePath)) critical.Add("sourcePath");

        if (string.IsNullOrWhiteSpace(handoff.PreviewWorkspaceStatePath)) important.Add("previewWorkspaceStatePath");
        if (string.IsNullOrWhiteSpace(handoff.ComparisonHistoryPath)) important.Add("comparisonHistoryPath");
        if (string.IsNullOrWhiteSpace(handoff.RouteValidationReportPath)) important.Add("routeValidationReportPath");

        if (string.IsNullOrWhiteSpace(handoff.HistoryStableHash)) optional.Add("sceneAwareStableHash");

        return new SceneAwareRouteADetailHandoffGap(
            CriticalMissingFields: critical,
            ImportantMissingFields: important,
            OptionalMissingFields: optional,
            RecommendedSchemaFields: [
                "compareSessionId",
                "oldSnapshotId",
                "newSnapshotId",
                "previewWorkspaceStatePath",
                "sceneAwareStableHash",
                "sourceKind"
            ]);
    }

    private static SceneAwareSceneCandidate ResolveBestSceneCandidate(List<SceneAwarePropertyReadResult> sceneCandidates)
    {
        var best = sceneCandidates.FirstOrDefault(x => x.ReadSucceeded && !string.IsNullOrWhiteSpace(x.ValuePreview) && x.ValuePreview != "null");
        if (best is null)
        {
            return new SceneAwareSceneCandidate(false, "None", "", "", "", "");
        }

        return new SceneAwareSceneCandidate(
            Found: true,
            Confidence: "Medium",
            SourceObjectType: best.DeclaringType,
            PropertyName: best.PropertyName,
            ValueType: best.ValueTypeFullName,
            ValuePreview: best.ValuePreview);
    }

    private static SceneAwareTimelineCollectionCandidate ResolveBestCollectionCandidate(List<SceneAwarePropertyReadResult> collectionCandidates)
    {
        var best = collectionCandidates
            .Where(x => x.ReadSucceeded && x.CollectionCount is not null)
            .OrderByDescending(x => x.CollectionCount)
            .FirstOrDefault();
        if (best is null)
        {
            return new SceneAwareTimelineCollectionCandidate(false, "None", "", "", null, [], [], [], [], [], [], [], []);
        }

        return new SceneAwareTimelineCollectionCandidate(
            Found: true,
            Confidence: "Medium",
            SourceObjectType: best.DeclaringType,
            PropertyName: best.PropertyName,
            Count: best.CollectionCount,
            SampleItemTypes: best.SampleItemTypes,
            SampleItemToString: best.SampleItemToString,
            FrameLikePropertyNames: best.FrameLikePropertyNames,
            LayerLikePropertyNames: best.LayerLikePropertyNames,
            TextLikePropertyNames: best.TextLikePropertyNames,
            StartLikePropertyNames: best.StartLikePropertyNames,
            EndLikePropertyNames: best.EndLikePropertyNames,
            DurationLikePropertyNames: best.DurationLikePropertyNames);
    }

    private static SceneAwareTimelineFingerprint BuildTimelineFingerprintCandidate(SceneAwareTimelineCollectionCandidate collectionCandidate, int getterErrorCount)
    {
        var itemTypes = collectionCandidate.SampleItemTypes
            .GroupBy(x => x, StringComparer.Ordinal)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var layerHist = collectionCandidate.LayerLikePropertyNames
            .GroupBy(x => x, StringComparer.Ordinal)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var textHist = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["HasTextLikeField"] = collectionCandidate.TextLikePropertyNames.Count > 0 ? 1 : 0,
            ["NoTextLikeField"] = collectionCandidate.TextLikePropertyNames.Count > 0 ? 0 : 1,
        };

        var stableText = BuildStableText(collectionCandidate.Count ?? 0, null, null, null, null, itemTypes, layerHist, textHist);
        var stableHash = ComputeSha256(stableText);
        var confidence = collectionCandidate.Count is > 0
            ? (collectionCandidate.LayerLikePropertyNames.Count > 0 || collectionCandidate.FrameLikePropertyNames.Count > 0 ? "Medium" : "Low")
            : "None";
        return new SceneAwareTimelineFingerprint(
            ItemCount: collectionCandidate.Count ?? 0,
            LayerCount: null,
            MinFrame: null,
            MaxFrame: null,
            TotalDuration: null,
            ItemTypeHistogram: itemTypes,
            LayerHistogram: layerHist,
            TextPresenceHistogram: textHist,
            SampleItemTypes: collectionCandidate.SampleItemTypes,
            SampleItemPreviews: collectionCandidate.SampleItemToString.Select(x => x.Length > 80 ? x[..80] : x).ToList(),
            StableText: stableText,
            StableHash: stableHash,
            Confidence: confidence);
    }

    private static SceneAwareSceneIdentityCandidate BuildSceneIdentityCandidate(SceneAwareSceneCandidate sceneCandidate, SceneAwareTimelineCandidate? best, SceneAwareTimelineFingerprint fingerprint)
    {
        var sceneName = sceneCandidate.Found ? sceneCandidate.ValuePreview : "<unknown>";
        var sceneIndex = best?.SceneIndex;
        var confidence = sceneCandidate.Found
            ? "High"
            : fingerprint.ItemCount > 0 && (fingerprint.LayerCount is not null || fingerprint.ItemTypeHistogram.Count > 0) ? "Medium"
            : fingerprint.ItemCount > 0 ? "Low" : "None";
        return new SceneAwareSceneIdentityCandidate(
            SceneName: sceneName,
            SceneIndex: sceneIndex,
            SourceKind: sceneCandidate.Found ? "SurfaceInventory" : "TimelineCandidateFallback",
            SourceProperty: sceneCandidate.PropertyName,
            TimelineFingerprintHash: fingerprint.StableHash,
            ItemCount: fingerprint.ItemCount,
            LayerCount: fingerprint.LayerCount,
            MinFrame: fingerprint.MinFrame,
            MaxFrame: fingerprint.MaxFrame,
            Confidence: confidence);
    }

    private static string BuildMarkdownReport(SceneAwareHistoryPreviewProbeResult r, string probePath, string summaryPath)
    {
        return $"""
# Scene-aware History Preview Investigation Report (Step 3)

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

## Surface Inventory Summary
- scannedObjectCount: {r.SurfaceInventory.ScannedObjectCount}
- propertyCount: {r.SurfaceInventory.PropertyCount}
- readablePropertyCount: {r.SurfaceInventory.ReadablePropertyCount}
- sceneCandidateCount: {r.SurfaceInventory.SceneCandidateCount}
- collectionCandidateCount: {r.SurfaceInventory.CollectionCandidateCount}
- frameCandidateCount: {r.SurfaceInventory.FrameCandidateCount}
- selectionCandidateCount: {r.SurfaceInventory.SelectionCandidateCount}
- getterErrorCount: {r.SurfaceInventory.GetterErrorCount}

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

## Best Scene Candidate
- found: {r.BestSceneCandidate.Found}
- confidence: {r.BestSceneCandidate.Confidence}
- sourceObjectType: {r.BestSceneCandidate.SourceObjectType}
- propertyName: {r.BestSceneCandidate.PropertyName}
- valueType: {r.BestSceneCandidate.ValueType}
- valuePreview: {r.BestSceneCandidate.ValuePreview}

## Best Timeline Collection Candidate
- found: {r.BestTimelineCollectionCandidate.Found}
- confidence: {r.BestTimelineCollectionCandidate.Confidence}
- sourceObjectType: {r.BestTimelineCollectionCandidate.SourceObjectType}
- propertyName: {r.BestTimelineCollectionCandidate.PropertyName}
- count: {r.BestTimelineCollectionCandidate.Count?.ToString() ?? "(null)"}

## Timeline Fingerprint
- itemCount: {r.TimelineFingerprintDetails.ItemCount}
- layerCount: {r.TimelineFingerprintDetails.LayerCount?.ToString() ?? "(null)"}
- minFrame: {r.TimelineFingerprintDetails.MinFrame?.ToString() ?? "(null)"}
- maxFrame: {r.TimelineFingerprintDetails.MaxFrame?.ToString() ?? "(null)"}
- stableHash: {r.TimelineFingerprintDetails.StableHash}
- confidence: {r.TimelineFingerprintDetails.Confidence}

## Scene Identity Candidate
- sceneName: {r.SceneIdentityCandidate.SceneName}
- sceneIndex: {r.SceneIdentityCandidate.SceneIndex?.ToString() ?? "(null)"}
- sourceKind: {r.SceneIdentityCandidate.SourceKind}
- sourceProperty: {r.SceneIdentityCandidate.SourceProperty}
- confidence: {r.SceneIdentityCandidate.Confidence}

## Output Files
- probe: {probePath}
- summary: {summaryPath}

## Fingerprint Safety
- maxItemsScanned: {r.FingerprintSafety.MaxItemsScanned}
- actualItemsScanned: {r.FingerprintSafety.ActualItemsScanned}
- getterErrorCount: {r.FingerprintSafety.GetterErrorCount}
- pathExcludedFromHash: {r.FingerprintSafety.PathExcludedFromHash}
- textBodyExcludedFromHash: {r.FingerprintSafety.TextBodyExcludedFromHash}

## Step 5: Snapshot / History Matching Foundation
- sourceCount: {r.HistoryMatching.SourceCount}
- readSucceededCount: {r.HistoryMatching.ReadSucceededCount}
- metadataCandidateCount: {r.HistoryMatching.MetadataCandidateCount}
- matchCandidateCount: {r.HistoryMatching.MatchCandidateCount}
- bestMatchScore: {r.HistoryMatching.BestMatchScore}
- bestMatchConfidence: {r.HistoryMatching.BestMatchConfidence}
- historyLinkFeasible: {r.HistoryMatching.HistoryLinkFeasible}

## Best History Match
- sourceKind: {r.BestHistoryMatchCandidate?.SourceKind ?? "(none)"}
- sourceFileName: {r.BestHistoryMatchCandidate?.SourceFileName ?? "(none)"}
- score: {r.BestHistoryMatchCandidate?.Score.ToString() ?? "0"}
- confidence: {r.BestHistoryMatchCandidate?.Confidence ?? "None"}

## Step 6: Scene-aware History List Preview
- previewItemCount: {r.HistoryPreview.PreviewItemCount}
- bestPreviewItemScore: {r.HistoryPreview.BestPreviewItemScore}
- bestPreviewItemConfidence: {r.HistoryPreview.BestPreviewItemConfidence}
- hasHighConfidenceMatch: {r.HistoryPreview.HasHighConfidenceMatch}
- routeADetailHandoffPrepared: {r.HistoryPreview.RouteADetailHandoffPrepared}
- routeADetailHandoffImplemented: no

## Step 7A: RouteA Detail Diff Handoff Foundation
- prepared: {r.RouteADetailHandoff.Prepared}
- canOpen: {r.RouteADetailHandoff.CanOpen}
- reason: {r.RouteADetailHandoff.Reason}
- sourceKind: {r.RouteADetailHandoff.SourceKind}
- sourceFileName: {r.RouteADetailHandoff.SourceFileName}
- snapshotId: {r.RouteADetailHandoff.SnapshotId ?? "(none)"}
- compareSessionId: {r.RouteADetailHandoff.CompareSessionId ?? "(none)"}
- availableFields: {(r.RouteADetailHandoff.AvailableFields.Count == 0 ? "(none)" : string.Join(", ", r.RouteADetailHandoff.AvailableFields))}
- missingFields: {(r.RouteADetailHandoff.MissingFields.Count == 0 ? "(none)" : string.Join(", ", r.RouteADetailHandoff.MissingFields))}
- warnings: {(r.RouteADetailHandoff.Warnings.Count == 0 ? "(none)" : string.Join(" | ", r.RouteADetailHandoff.Warnings))}

## Step 7A.5: RouteA Handoff Metadata Gap
- criticalMissingFields: {(r.RouteADetailHandoffGap.CriticalMissingFields.Count == 0 ? "(none)" : string.Join(", ", r.RouteADetailHandoffGap.CriticalMissingFields))}
- importantMissingFields: {(r.RouteADetailHandoffGap.ImportantMissingFields.Count == 0 ? "(none)" : string.Join(", ", r.RouteADetailHandoffGap.ImportantMissingFields))}
- optionalMissingFields: {(r.RouteADetailHandoffGap.OptionalMissingFields.Count == 0 ? "(none)" : string.Join(", ", r.RouteADetailHandoffGap.OptionalMissingFields))}
- recommendedSchemaFields: {(r.RouteADetailHandoffGap.RecommendedSchemaFields.Count == 0 ? "(none)" : string.Join(", ", r.RouteADetailHandoffGap.RecommendedSchemaFields))}
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
    SceneAwareBestTimelineCandidate BestYmmTimelineCandidate,
    SceneAwareSurfaceInventorySummary SurfaceInventory,
    SceneAwareSceneCandidate BestSceneCandidate,
    SceneAwareTimelineCollectionCandidate BestTimelineCollectionCandidate,
    SceneAwareTimelineFingerprint TimelineFingerprintDetails,
    SceneAwareSceneIdentityCandidate SceneIdentityCandidate,
    SceneAwareHistoryMatchingSummary HistoryMatching,
    SceneAwareHistoryPreviewSummaryMetrics HistoryPreview,
    IReadOnlyList<SceneAwareHistoryPreviewItem> HistoryPreviewItems,
    SceneAwareRouteADetailHandoffCandidate RouteADetailHandoff,
    SceneAwareRouteADetailHandoffGap RouteADetailHandoffGap,
    SceneAwareHistoryMatchCandidate? BestHistoryMatchCandidate);

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
    SceneAwareSurfaceInventorySummary SurfaceInventory,
    IReadOnlyList<SceneAwareScannedObjectReport> ScannedObjects,
    SceneAwareSceneCandidate BestSceneCandidate,
    SceneAwareTimelineCollectionCandidate BestTimelineCollectionCandidate,
    IReadOnlyList<SceneAwarePropertyReadResult> GetterErrors,
    SceneAwareTimelineFingerprint TimelineFingerprintDetails,
    SceneAwareSceneIdentityCandidate SceneIdentityCandidate,
    SceneAwareFingerprintSafety FingerprintSafety,
    SceneAwareHistoryMatchingSummary HistoryMatching,
    IReadOnlyList<SceneAwareHistorySource> HistorySources,
    IReadOnlyList<SceneAwareHistoryMatchCandidate> HistoryMatchCandidates,
    SceneAwareHistoryPreviewSummaryMetrics HistoryPreview,
    IReadOnlyList<SceneAwareHistoryPreviewItem> HistoryPreviewItems,
    SceneAwareRouteADetailHandoffCandidate RouteADetailHandoff,
    SceneAwareRouteADetailHandoffGap RouteADetailHandoffGap,
    SceneAwareHistoryMatchCandidate? BestHistoryMatchCandidate,
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

internal sealed record SceneAwareSurfaceInventorySummary(
    int ScannedObjectCount,
    int PropertyCount,
    int ReadablePropertyCount,
    int SceneCandidateCount,
    int CollectionCandidateCount,
    int FrameCandidateCount,
    int SelectionCandidateCount,
    int GetterErrorCount);

internal sealed record SceneAwareScannedObjectReport(
    string SourceKind,
    string Type,
    IReadOnlyList<SceneAwarePropertyReadResult> Properties);

internal sealed record SceneAwarePropertyReadResult(
    string PropertyName,
    string PropertyTypeFullName,
    bool CanRead,
    bool CanWrite,
    bool GetterIsPublic,
    int IndexParametersCount,
    string DeclaringType,
    string Category,
    bool ReadAttempted,
    bool ReadSucceeded,
    string ReadErrorType,
    string ReadErrorMessage,
    string ValueKind,
    string ValueTypeFullName,
    string ValuePreview,
    int? CollectionCount,
    IReadOnlyList<string> SampleItemTypes,
    IReadOnlyList<string> SampleItemToString,
    IReadOnlyList<string> FrameLikePropertyNames,
    IReadOnlyList<string> LayerLikePropertyNames,
    IReadOnlyList<string> TextLikePropertyNames,
    IReadOnlyList<string> StartLikePropertyNames,
    IReadOnlyList<string> EndLikePropertyNames,
    IReadOnlyList<string> DurationLikePropertyNames);

internal sealed record SceneAwareSceneCandidate(
    bool Found,
    string Confidence,
    string SourceObjectType,
    string PropertyName,
    string ValueType,
    string ValuePreview);

internal sealed record SceneAwareTimelineCollectionCandidate(
    bool Found,
    string Confidence,
    string SourceObjectType,
    string PropertyName,
    int? Count,
    IReadOnlyList<string> SampleItemTypes,
    IReadOnlyList<string> SampleItemToString,
    IReadOnlyList<string> FrameLikePropertyNames,
    IReadOnlyList<string> LayerLikePropertyNames,
    IReadOnlyList<string> TextLikePropertyNames,
    IReadOnlyList<string> StartLikePropertyNames,
    IReadOnlyList<string> EndLikePropertyNames,
    IReadOnlyList<string> DurationLikePropertyNames);

internal sealed record SceneAwareTimelineFingerprint(
    int ItemCount,
    int? LayerCount,
    long? MinFrame,
    long? MaxFrame,
    long? TotalDuration,
    IReadOnlyDictionary<string, int> ItemTypeHistogram,
    IReadOnlyDictionary<string, int> LayerHistogram,
    IReadOnlyDictionary<string, int> TextPresenceHistogram,
    IReadOnlyList<string> SampleItemTypes,
    IReadOnlyList<string> SampleItemPreviews,
    string StableText,
    string StableHash,
    string Confidence);

internal sealed record SceneAwareSceneIdentityCandidate(
    string SceneName,
    int? SceneIndex,
    string SourceKind,
    string SourceProperty,
    string TimelineFingerprintHash,
    int ItemCount,
    int? LayerCount,
    long? MinFrame,
    long? MaxFrame,
    string Confidence);

internal sealed record SceneAwareFingerprintSafety(
    int MaxItemsScanned,
    int ActualItemsScanned,
    int GetterErrorCount,
    bool PathExcludedFromHash,
    bool TextBodyExcludedFromHash,
    bool ReadOnly);

internal sealed record SceneAwareHistorySource(
    string SourcePath,
    string SourceFileName,
    string SourceKind,
    bool Exists,
    bool ReadSucceeded,
    string ReadErrorType,
    string ReadErrorMessage,
    DateTimeOffset? ModifiedAt,
    long SizeBytes);

internal sealed record SceneAwareHistoryMatchCandidate(
    string SourceKind,
    string SourceFileName,
    string SourcePath,
    DateTimeOffset? ModifiedAt,
    string? SceneName,
    int? SceneIndex,
    string? StableHash,
    int? ItemCount,
    int? LayerCount,
    long? MinFrame,
    long? MaxFrame,
    int Score,
    string Confidence,
    IReadOnlyList<string> MatchReasons,
    IReadOnlyList<string> MissingFields);

internal sealed record SceneAwareHistoryMatchingSummary(
    int SourceCount,
    int ReadSucceededCount,
    int MetadataCandidateCount,
    int MatchCandidateCount,
    int BestMatchScore,
    string BestMatchConfidence,
    bool HistoryLinkFeasible);

internal sealed record SceneAwareHistoryPreviewSummaryMetrics(
    int PreviewItemCount,
    int BestPreviewItemScore,
    string BestPreviewItemConfidence,
    bool HasHighConfidenceMatch,
    bool RouteADetailHandoffPrepared);

public sealed record SceneAwareHistoryPreviewItem(
    int Rank,
    string Title,
    string SourceKind,
    string SourceFileName,
    string SourcePath,
    DateTimeOffset? ModifiedAt,
    int Score,
    string Confidence,
    bool IsBestMatch,
    string SceneName,
    int? SceneIndex,
    string StableHash,
    int? ItemCount,
    int? LayerCount,
    long? MinFrame,
    long? MaxFrame,
    IReadOnlyList<string> MatchReasons,
    IReadOnlyList<string> MissingFields,
    string SummaryText,
    string DetailText);

internal sealed record SceneAwareRouteADetailHandoffCandidate(
    bool Prepared,
    bool CanOpen,
    string Reason,
    string SourceKind,
    string SourceFileName,
    string SourcePath,
    string? SnapshotId,
    string? CompareSessionId,
    string? RouteValidationReportPath,
    string? PreviewWorkspaceStatePath,
    string? ComparisonHistoryPath,
    string RuntimeStableHash,
    string HistoryStableHash,
    int Score,
    string Confidence,
    IReadOnlyList<string> AvailableFields,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<string> Warnings,
    string SummaryText);

internal sealed record SceneAwareRouteAExtractedFields(
    string? SnapshotId,
    string? CompareSessionId,
    string? RouteValidationReportPath,
    string? PreviewWorkspaceStatePath,
    string? ComparisonHistoryPath,
    IReadOnlyList<string> Warnings);

internal sealed record SceneAwareRouteADetailHandoffGap(
    IReadOnlyList<string> CriticalMissingFields,
    IReadOnlyList<string> ImportantMissingFields,
    IReadOnlyList<string> OptionalMissingFields,
    IReadOnlyList<string> RecommendedSchemaFields);
