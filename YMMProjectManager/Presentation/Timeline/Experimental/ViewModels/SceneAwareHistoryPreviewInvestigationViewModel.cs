namespace YMMProjectManager.Presentation.Timeline.Experimental.ViewModels;

public sealed class SceneAwareHistoryPreviewInvestigationViewModel : ViewModelBase
{
    private string summaryText = "not-run";
    private string timelineInfoText = string.Empty;
    private string candidatesText = string.Empty;
    private string diagnosticsText = string.Empty;

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
            "windows:",
            string.Join(Environment.NewLine, result.Windows.Select(x => $"- {x.WindowType} title={x.Title} excluded={x.Excluded} reason={x.ExcludedReason}")),
            $"probe={result.ProbePath}",
            $"summary={result.SummaryPath}",
            $"report={result.ReportPath}",
        });
    }
}
