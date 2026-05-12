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
        CandidatesText = result.SnapshotHistoryCandidates.Count == 0
            ? "(none)"
            : string.Join(Environment.NewLine, result.SnapshotHistoryCandidates.Select(x => $"- {x}"));
        DiagnosticsText = string.Join(Environment.NewLine, new[]
        {
            $"timelineViewType={result.TimelineViewType}",
            $"timelineVmType={result.TimelineViewModelType}",
            $"ownerWindowType={result.OwnerWindowType}",
            $"ownerDataContextType={result.OwnerDataContextType}",
            $"fingerprint={result.TimelineFingerprint}",
            $"probe={result.ProbePath}",
            $"summary={result.SummaryPath}",
            $"report={result.ReportPath}",
        });
    }
}
