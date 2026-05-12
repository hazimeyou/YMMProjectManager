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
        RouteADetailHandoffStatusText = $"prepared={result.RouteADetailHandoff.Prepared}, canOpen={result.RouteADetailHandoff.CanOpen}, reason={result.RouteADetailHandoff.Reason}, source={result.RouteADetailHandoff.SourceFileName}, snapshotId={result.RouteADetailHandoff.SnapshotId ?? "(none)"}, compareSessionId={result.RouteADetailHandoff.CompareSessionId ?? "(none)"}, available=[{string.Join(", ", result.RouteADetailHandoff.AvailableFields)}], missing=[{string.Join(", ", result.RouteADetailHandoff.MissingFields)}], warnings=[{string.Join(" | ", result.RouteADetailHandoff.Warnings)}]";
        CanOpenRouteADetailDiff = false; // Step 7A keeps open action disabled (dry-run only)

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
            "windows:",
            string.Join(Environment.NewLine, result.Windows.Select(x => $"- {x.WindowType} title={x.Title} excluded={x.Excluded} reason={x.ExcludedReason}")),
            $"probe={result.ProbePath}",
            $"summary={result.SummaryPath}",
            $"report={result.ReportPath}",
        });
    }
}
