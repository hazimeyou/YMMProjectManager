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

        var fingerprint = BuildTimelineFingerprint(sceneName, itemCount, layerCount, currentFrame);

        var result = new SceneAwareHistoryPreviewProbeResult(
            Route: "RouteB",
            Investigation: "SceneAwareHistoryPreview",
            Step: "TimelineViewModelSurfaceInventory",
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
            GetterErrors: getterErrors);

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
            BestYmmTimelineCandidate: result.BestYmmTimelineCandidate,
            SurfaceInventory: result.SurfaceInventory,
            BestSceneCandidate: result.BestSceneCandidate,
            BestTimelineCollectionCandidate: result.BestTimelineCollectionCandidate);
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
    SceneAwareBestTimelineCandidate BestYmmTimelineCandidate,
    SceneAwareSurfaceInventorySummary SurfaceInventory,
    SceneAwareSceneCandidate BestSceneCandidate,
    SceneAwareTimelineCollectionCandidate BestTimelineCollectionCandidate);

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
