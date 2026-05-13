namespace YMMProjectManager.Presentation.Timeline.Experimental.ViewModels;

public sealed class SceneAwareHistoryPreviewInvestigationViewModel : ViewModelBase
{
    private string summaryText = "not-run";
    private string timelineInfoText = string.Empty;
    private string candidatesText = string.Empty;
    private string diagnosticsText = string.Empty;
    private SceneAwareHistoryPreviewItem? selectedHistoryPreviewItem;
    private string selectedHistoryPreviewItemDetailText = "(none)";
    private string historyPreviewSummaryText = "(none)";
    private string routeADetailHandoffStatusText = "(not-prepared)";
    private bool canOpenRouteADetailDiff;
    private string routeADetailOpenResultText = "(not-attempted)";
    private string rcStatusText = "(unknown)";
    private bool previewFeatureAvailable;
    private bool previewFeatureEnabled;
    private string previewFeatureMode = "InvestigationPreview";
    private string previewFeatureStatusText = "(unknown)";
    private string heavyProjectWarningText = string.Empty;

    public string SummaryText
    {
        get => summaryText;
        set => SetProperty(ref summaryText, value);
    }

    public string TimelineInfoText
    {
        get => timelineInfoText;
        set => SetProperty(ref timelineInfoText, value);
    }

    public string CandidatesText
    {
        get => candidatesText;
        set => SetProperty(ref candidatesText, value);
    }

    public string DiagnosticsText
    {
        get => diagnosticsText;
        set => SetProperty(ref diagnosticsText, value);
    }

    public ObservableCollection<SceneAwareHistoryPreviewItem> HistoryPreviewItems { get; } = [];

    public SceneAwareHistoryPreviewItem? SelectedHistoryPreviewItem
    {
        get => selectedHistoryPreviewItem;
        set
        {
            if (!SetProperty(ref selectedHistoryPreviewItem, value))
            {
                return;
            }

            SelectedHistoryPreviewItemDetailText = value is null
                ? "(none)"
                : $"{value.Title}{Environment.NewLine}{value.DetailText}{Environment.NewLine}SourcePath: {value.SourcePath}{Environment.NewLine}RouteA Detail Handoff: not implemented in Step 6";
        }
    }

    public string SelectedHistoryPreviewItemDetailText
    {
        get => selectedHistoryPreviewItemDetailText;
        set => SetProperty(ref selectedHistoryPreviewItemDetailText, value);
    }

    public string HistoryPreviewSummaryText
    {
        get => historyPreviewSummaryText;
        set => SetProperty(ref historyPreviewSummaryText, value);
    }

    public string RouteADetailHandoffStatusText
    {
        get => routeADetailHandoffStatusText;
        set => SetProperty(ref routeADetailHandoffStatusText, value);
    }

    public bool CanOpenRouteADetailDiff
    {
        get => canOpenRouteADetailDiff;
        set => SetProperty(ref canOpenRouteADetailDiff, value);
    }

    public string RouteADetailOpenResultText
    {
        get => routeADetailOpenResultText;
        set => SetProperty(ref routeADetailOpenResultText, value);
    }

    public string RcStatusText
    {
        get => rcStatusText;
        set => SetProperty(ref rcStatusText, value);
    }

    public bool PreviewFeatureAvailable
    {
        get => previewFeatureAvailable;
        set => SetProperty(ref previewFeatureAvailable, value);
    }

    public bool PreviewFeatureEnabled
    {
        get => previewFeatureEnabled;
        set => SetProperty(ref previewFeatureEnabled, value);
    }

    public string PreviewFeatureMode
    {
        get => previewFeatureMode;
        set => SetProperty(ref previewFeatureMode, value);
    }

    public string PreviewFeatureStatusText
    {
        get => previewFeatureStatusText;
        set => SetProperty(ref previewFeatureStatusText, value);
    }

    public string HeavyProjectWarningText
    {
        get => heavyProjectWarningText;
        set => SetProperty(ref heavyProjectWarningText, value);
    }

    internal void Apply(SceneAwareHistoryPreviewProbeResult result)
    {
        SummaryText = $"sceneDetected={result.CurrentSceneDetected}, linkFeasible={result.SceneHistoryLinkFeasible}, confidence={result.Confidence}";
        TimelineInfoText = $"scene={result.SceneName} / index={result.SceneIndex?.ToString() ?? "?"} / items={result.TimelineItemCount?.ToString() ?? "?"} / layers={result.LayerCount?.ToString() ?? "?"} / selected={result.SelectedItemCount?.ToString() ?? "?"} / frame={result.CurrentFrame?.ToString() ?? "?"}";

        CandidatesText = result.TimelineCandidates.Count == 0
            ? "(none)"
            : string.Join(Environment.NewLine, result.TimelineCandidates
                .OrderByDescending(x => x.Score)
                .Take(20)
                .Select(x => $"[{x.Confidence}] score={x.Score} type={x.ElementType} vm={x.DataContextType} owner={x.OwnerWindowType} excluded={x.Excluded} reason={x.ExcludedReason}"));

        HistoryPreviewItems.Clear();
        foreach (var item in result.HistoryPreviewItems)
        {
            HistoryPreviewItems.Add(item);
        }
        SelectedHistoryPreviewItem = HistoryPreviewItems.FirstOrDefault();
        HistoryPreviewSummaryText = $"previewItemCount={result.HistoryPreview.PreviewItemCount}, bestScore={result.HistoryPreview.BestPreviewItemScore}, bestConfidence={result.HistoryPreview.BestPreviewItemConfidence}, hasHighConfidenceMatch={result.HistoryPreview.HasHighConfidenceMatch}, routeADetailHandoffPrepared={result.HistoryPreview.RouteADetailHandoffPrepared}";
        RouteADetailHandoffStatusText = $"prepared={result.RouteADetailHandoff.Prepared}, canOpen={result.RouteADetailHandoff.CanOpen}, reason={result.RouteADetailHandoff.Reason}, source={result.RouteADetailHandoff.SourceFileName}, snapshotId={result.RouteADetailHandoff.SnapshotId ?? "(none)"}, compareSessionId={result.RouteADetailHandoff.CompareSessionId ?? "(none)"}, available=[{string.Join(", ", result.RouteADetailHandoff.AvailableFields)}], missing=[{string.Join(", ", result.RouteADetailHandoff.MissingFields)}], warnings=[{string.Join(" | ", result.RouteADetailHandoff.Warnings)}], hasSnapshotPair={result.RouteAOpenReadiness.HasSnapshotPair}, snapshotResolved={result.SnapshotPairResolution.Resolved}";
        CanOpenRouteADetailDiff = result.RouteAOpenReadiness.Prepared
            && result.RouteAOpenReadiness.CanOpen
            && result.RouteAOpenReadiness.HasSnapshotPair
            && result.SnapshotPairResolution.Resolved
            && !string.IsNullOrWhiteSpace(result.SnapshotPairResolution.OldSnapshotHash)
            && !string.IsNullOrWhiteSpace(result.SnapshotPairResolution.NewSnapshotHash)
            && result.DefaultDisabled
            && result.RouteAPreserved
            && result.FallbackPreserved
            && !result.RuntimeMutation
            && !result.InputInjection
            && !result.ProductionEmbedding;
        RouteADetailOpenResultText = "Ready for read-only dry-run open. Click button to record safe open attempt.";
        RcStatusText = $"rc={result.RouteBInvestigationRc.RcVersion}, prepared={result.RouteBInvestigationReadiness.Prepared}, confidence={result.RouteBInvestigationReadiness.Confidence}, openMode={result.RouteBInvestigationRc.DetailOpenMode}, viewerWired={result.RouteBInvestigationRc.ViewerWired}, defaultDisabled={result.DefaultDisabled}, fallbackPreserved={result.FallbackPreserved}, routeAPreserved={result.RouteAPreserved}";
        PreviewFeatureAvailable = result.PreviewFeatureGate.Prepared && result.PreviewFeatureGate.PreviewOnly;
        PreviewFeatureEnabled = result.PreviewFeatureGate.Enabled;
        PreviewFeatureMode = result.PreviewUiConsolidation.Mode;
        PreviewFeatureStatusText = $"prepared={result.PreviewFeatureGate.Prepared}, enabled={result.PreviewFeatureGate.Enabled}, previewOnly={result.PreviewFeatureGate.PreviewOnly}, investigationRc={result.PreviewFeatureGate.InvestigationRc}, feature={result.PreviewFeatureGate.FeatureIdentity}/{result.PreviewFeatureGate.FeatureVersion}, openMode={result.PreviewFeatureGate.OpenMode}, viewerWired={result.PreviewFeatureGate.ViewerWired}, canEnablePreviewUi={result.PreviewFeatureReadiness.CanEnablePreviewUi}, readiness={result.PreviewFeatureReadiness.Confidence}";
        HeavyProjectWarningText = result.HeavyProjectHeuristics.IsHeavyProject
            ? "Heavy Project Detected: preview virtualization recommended."
            : string.Empty;

        DiagnosticsText = string.Join(Environment.NewLine, new[]
        {
            $"windowScan total={result.WindowScan.TotalWindows} excluded={result.WindowScan.ExcludedWindows} candidates={result.WindowScan.CandidateWindows}",
            $"surface scannedObjects={result.SurfaceInventory.ScannedObjectCount} props={result.SurfaceInventory.PropertyCount} readable={result.SurfaceInventory.ReadablePropertyCount}",
            $"surface sceneCandidates={result.SurfaceInventory.SceneCandidateCount} collectionCandidates={result.SurfaceInventory.CollectionCandidateCount} frameCandidates={result.SurfaceInventory.FrameCandidateCount} selectionCandidates={result.SurfaceInventory.SelectionCandidateCount}",
            $"surface getterErrors={result.SurfaceInventory.GetterErrorCount}",
            $"best found={result.BestYmmTimelineCandidate.Found} score={result.BestYmmTimelineCandidate.Score} confidence={result.BestYmmTimelineCandidate.Confidence}",
            $"best type={result.BestYmmTimelineCandidate.ElementType}",
            $"best vm={result.BestYmmTimelineCandidate.DataContextType}",
            $"best owner={result.BestYmmTimelineCandidate.OwnerWindowType}",
            $"best scene found={result.BestSceneCandidate.Found} source={result.BestSceneCandidate.SourceObjectType}.{result.BestSceneCandidate.PropertyName} value={result.BestSceneCandidate.ValuePreview}",
            $"best collection found={result.BestTimelineCollectionCandidate.Found} source={result.BestTimelineCollectionCandidate.SourceObjectType}.{result.BestTimelineCollectionCandidate.PropertyName} count={result.BestTimelineCollectionCandidate.Count?.ToString() ?? "?"}",
            $"fingerprint hash={result.TimelineFingerprintDetails.StableHash} itemCount={result.TimelineFingerprintDetails.ItemCount} confidence={result.TimelineFingerprintDetails.Confidence}",
            $"sceneIdentity scene={result.SceneIdentityCandidate.SceneName} index={result.SceneIdentityCandidate.SceneIndex?.ToString() ?? "?"} confidence={result.SceneIdentityCandidate.Confidence}",
            $"fingerprintSafety max={result.FingerprintSafety.MaxItemsScanned} actual={result.FingerprintSafety.ActualItemsScanned} getterErrors={result.FingerprintSafety.GetterErrorCount}",
            $"history sourceCount={result.HistoryMatching.SourceCount} readOk={result.HistoryMatching.ReadSucceededCount} metadataCandidates={result.HistoryMatching.MetadataCandidateCount}",
            $"history matchCandidates={result.HistoryMatching.MatchCandidateCount} bestScore={result.HistoryMatching.BestMatchScore} bestConfidence={result.HistoryMatching.BestMatchConfidence} linkFeasible={result.HistoryMatching.HistoryLinkFeasible}",
            $"history best source={result.BestHistoryMatchCandidate?.SourceKind ?? "(none)"} file={result.BestHistoryMatchCandidate?.SourceFileName ?? "(none)"} score={result.BestHistoryMatchCandidate?.Score.ToString() ?? "0"}",
            $"heavyProject isHeavy={result.HeavyProjectHeuristics.IsHeavyProject} historySources={result.HeavyProjectHeuristics.HistorySourceCount} snapshotRepo={result.HeavyProjectHeuristics.SnapshotRepositoryCount} recommendedVirtualization={result.HeavyProjectHeuristics.RecommendedVirtualization}",
            $"performance totalMs={result.PreviewPerformanceDiagnostics.TotalProbeMs} historyScanMs={result.PreviewPerformanceDiagnostics.HistoryScanMs} matchMs={result.PreviewPerformanceDiagnostics.HistoryMatchingMs}",
            $"previewListSafety limit={result.PreviewListSafety.PreviewItemLimit} total={result.PreviewListSafety.TotalCandidates} displayed={result.PreviewListSafety.DisplayedCandidates} truncated={result.PreviewListSafety.Truncated}",
            $"handoffGap critical=[{string.Join(", ", result.RouteADetailHandoffGap.CriticalMissingFields)}] important=[{string.Join(", ", result.RouteADetailHandoffGap.ImportantMissingFields)}] optional=[{string.Join(", ", result.RouteADetailHandoffGap.OptionalMissingFields)}]",
            "windows:",
            string.Join(Environment.NewLine, result.Windows.Select(x => $"- {x.WindowType} title={x.Title} excluded={x.Excluded} reason={x.ExcludedReason}")),
            $"probe={result.ProbePath}",
            $"summary={result.SummaryPath}",
            $"report={result.ReportPath}",
        });
    }

    public void OpenRouteADetailPreviewDryRun()
    {
        if (!CanOpenRouteADetailDiff)
        {
            RouteADetailOpenResultText = "Blocked: readiness false (safe guard)";
            return;
        }

        RouteADetailOpenResultText = $"DryRun Open Attempt: readOnly=True, viewerWired=False, reason=viewer invocation not wired yet, time={DateTimeOffset.Now:O}";
    }
}
