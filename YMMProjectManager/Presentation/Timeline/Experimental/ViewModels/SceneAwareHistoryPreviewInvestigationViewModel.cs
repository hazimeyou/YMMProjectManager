namespace YMMProjectManager.Presentation.Timeline.Experimental.ViewModels;

public sealed record RouteADetailPreviewOpenRequest(
    string SourceKind,
    string SelectedCandidateId,
    string? OldSnapshotHash,
    string? NewSnapshotHash,
    bool ReadOnly,
    bool AllowDiffApply,
    bool AllowHistoryRestore,
    bool AllowRuntimeMutation,
    string OpenMode,
    bool ManualButtonClick);

public sealed record RouteADetailPreviewOpenResult(
    bool OpenAttempted,
    bool OpenSucceeded,
    bool FallbackToDryRun,
    string ErrorMessage,
    bool ViewerWired,
    string OpenMode,
    string SelectedCandidateId,
    string? OldSnapshotHash,
    string? NewSnapshotHash);

public sealed class SceneAwareHistoryPreviewInvestigationViewModel : ViewModelBase
{
    private readonly string diagnosticsDirectory;
    private SceneAwareHistoryPreviewProbeResult? latestResult;
    private Func<RouteADetailPreviewOpenRequest, RouteADetailPreviewOpenResult>? openHandler;
    private RouteADetailPreviewOpenResult? lastRouteADetailOpenResult;

    private string summaryText = "Preview candidate (read-only / default disabled)";
    private string currentSceneText = "(not-run)";
    private string relatedHistoryStatusText = "(not-run)";
    private string matchReasonText = "(Select a history candidate)";
    private string routeADetailPreviewText = "(not-run)";
    private string diagnosticsSafetyText = "(not-run)";
    private string diagnosticsText = string.Empty;
    private SceneAwareHistoryPreviewItem? selectedHistoryPreviewItem;
    private string selectedHistoryPreviewItemDetailText = "(none)";
    private string historyPreviewSummaryText = "(none)";
    private bool canOpenRouteADetailDiff;
    private string routeADetailOpenResultText = "(not-attempted)";
    private string rcStatusText = "(unknown)";
    private string previewFeatureStatusText = "(unknown)";
    private string outputPathsText = "(not-run)";

    public string SummaryText { get => summaryText; set => SetProperty(ref summaryText, value); }
    public string CurrentSceneText { get => currentSceneText; set => SetProperty(ref currentSceneText, value); }
    public string RelatedHistoryStatusText { get => relatedHistoryStatusText; set => SetProperty(ref relatedHistoryStatusText, value); }
    public string MatchReasonText { get => matchReasonText; set => SetProperty(ref matchReasonText, value); }
    public string RouteADetailPreviewText { get => routeADetailPreviewText; set => SetProperty(ref routeADetailPreviewText, value); }
    public string DiagnosticsSafetyText { get => diagnosticsSafetyText; set => SetProperty(ref diagnosticsSafetyText, value); }
    public string DiagnosticsText { get => diagnosticsText; set => SetProperty(ref diagnosticsText, value); }
    public string SelectedHistoryPreviewItemDetailText { get => selectedHistoryPreviewItemDetailText; set => SetProperty(ref selectedHistoryPreviewItemDetailText, value); }
    public string HistoryPreviewSummaryText { get => historyPreviewSummaryText; set => SetProperty(ref historyPreviewSummaryText, value); }
    public bool CanOpenRouteADetailDiff { get => canOpenRouteADetailDiff; set => SetProperty(ref canOpenRouteADetailDiff, value); }
    public string RouteADetailOpenResultText { get => routeADetailOpenResultText; set => SetProperty(ref routeADetailOpenResultText, value); }
    public string RcStatusText { get => rcStatusText; set => SetProperty(ref rcStatusText, value); }
    public string PreviewFeatureStatusText { get => previewFeatureStatusText; set => SetProperty(ref previewFeatureStatusText, value); }
    public string OutputPathsText { get => outputPathsText; set => SetProperty(ref outputPathsText, value); }

    public ObservableCollection<SceneAwareHistoryPreviewItem> HistoryPreviewItems { get; } = [];

    public SceneAwareHistoryPreviewInvestigationViewModel(string diagnosticsDirectory)
    {
        this.diagnosticsDirectory = diagnosticsDirectory;
    }

    public SceneAwareHistoryPreviewItem? SelectedHistoryPreviewItem
    {
        get => selectedHistoryPreviewItem;
        set
        {
            if (!SetProperty(ref selectedHistoryPreviewItem, value)) return;
            SelectedHistoryPreviewItemDetailText = value is null
                ? "(none)"
                : $"{value.Title}{Environment.NewLine}{value.DetailText}{Environment.NewLine}SourcePath: {value.SourcePath}";
            MatchReasonText = value is null ? "(Select a history candidate)" : BuildMatchReasonText(value);
        }
    }

    public void SetOpenHandler(Func<RouteADetailPreviewOpenRequest, RouteADetailPreviewOpenResult> handler) => openHandler = handler;

    public void RerunInvestigation()
    {
        if (string.IsNullOrWhiteSpace(diagnosticsDirectory)) return;
        var refreshed = SceneAwareHistoryPreviewProbe.Run(diagnosticsDirectory, lastRouteADetailOpenResult);
        Apply(refreshed);
    }

    internal void Apply(SceneAwareHistoryPreviewProbeResult result)
    {
        latestResult = result;
        var probePath = Path.GetFullPath(result.ProbePath);
        var summaryPath = Path.GetFullPath(result.SummaryPath);
        var reportPath = Path.GetFullPath(result.ReportPath);
        OutputPathsText = $"probe={probePath}{Environment.NewLine}summary={summaryPath}{Environment.NewLine}report={reportPath}";

        SummaryText = "Preview candidate (Read-only / Experimental / Disabled by default)";
        CurrentSceneText = string.Join(Environment.NewLine, new[]
        {
            $"Current scene: {result.SceneIdentityCandidate.SceneName}",
            $"Timeline detection: {result.Confidence}",
            $"Item count: {result.TimelineItemCount?.ToString() ?? "?"}",
            $"History link: {(result.SceneHistoryLinkFeasible ? "Available" : "Unknown")}",
            $"Fingerprint: {(string.IsNullOrWhiteSpace(result.TimelineFingerprintDetails.StableHash) ? "Missing" : "Ready")}"
        });

        HistoryPreviewItems.Clear();
        foreach (var item in result.HistoryPreviewItems) HistoryPreviewItems.Add(item);
        SelectedHistoryPreviewItem = HistoryPreviewItems.FirstOrDefault();

        HistoryPreviewSummaryText = $"Related history: {result.HistoryPreview.PreviewItemCount} items / Best={result.HistoryPreview.BestPreviewItemConfidence}";
        RelatedHistoryStatusText = result.PreviewListSafety.Truncated
            ? $"Showing first 20 items (total candidates: {result.PreviewListSafety.TotalCandidates})"
            : $"Showing {result.PreviewListSafety.TotalCandidates} candidates";

        CanOpenRouteADetailDiff = result.PreviewFeatureGate.Prepared
            && !result.PreviewFeatureGate.Enabled
            && result.PreviewFeatureGate.PreviewOnly
            && result.SnapshotPairResolution.Resolved
            && result.SnapshotPairResolution.OldSnapshotFound
            && result.SnapshotPairResolution.NewSnapshotFound
            && result.RouteAOpenReadiness.CanOpen
            && result.DefaultDisabled
            && result.RouteAPreserved
            && result.FallbackPreserved
            && !result.RuntimeMutation
            && !result.InputInjection
            && !result.ProductionEmbedding;

        RouteADetailPreviewText = "Selected related history can be opened in RouteA detail viewer as read-only sandbox.";
        RouteADetailOpenResultText = "Manual open only. Diff apply / restore / runtime mutation are disabled.";
        RcStatusText = $"rc={result.RouteBFinalInvestigationRc.RcVersion}, viewerWired=False, openMode=ReadOnlyDryRun";
        PreviewFeatureStatusText = $"enabled={result.PreviewFeatureGate.Enabled}, previewOnly={result.PreviewFeatureGate.PreviewOnly}, viewerWired={result.PreviewFeatureGate.ViewerWired}, openMode={result.PreviewFeatureGate.OpenMode}";
        DiagnosticsSafetyText = "Preview feature: Disabled\nMode: Preview only\nRouteA viewer: Not wired\nSafety: Fallback preserved\nRuntime mutation: Disabled\nInput injection: Disabled";

        DiagnosticsText = string.Join(Environment.NewLine, new[]
        {
            $"previewListSafety limit={result.PreviewListSafety.PreviewItemLimit} total={result.PreviewListSafety.TotalCandidates} displayed={result.PreviewListSafety.DisplayedCandidates} truncated={result.PreviewListSafety.Truncated}",
            $"snapshotPair resolved={result.SnapshotPairResolution.Resolved} oldFound={result.SnapshotPairResolution.OldSnapshotFound} newFound={result.SnapshotPairResolution.NewSnapshotFound}",
            $"routeAOpen canOpen={result.RouteAOpenReadiness.CanOpen}",
            $"probe={probePath}",
            $"summary={summaryPath}",
            $"report={reportPath}",
        });
    }

    public void OpenRouteADetailPreviewDryRun()
    {
        if (latestResult is null || SelectedHistoryPreviewItem is null)
        {
            RouteADetailOpenResultText = "RouteA detail viewer cannot be opened. Select a history candidate first.";
            return;
        }

        if (!CanOpenRouteADetailDiff)
        {
            RouteADetailOpenResultText = "RouteA detail viewer cannot be opened. Check snapshot pair/readiness.";
            return;
        }

        if (openHandler is null)
        {
            RouteADetailOpenResultText = "Safety fallback: dry-run only (viewer open handler is not wired).";
            return;
        }

        var req = new RouteADetailPreviewOpenRequest(
            SourceKind: "RouteBSceneAwarePreview",
            SelectedCandidateId: SelectedHistoryPreviewItem.SourceFileName,
            OldSnapshotHash: latestResult.SnapshotPairResolution.OldSnapshotHash,
            NewSnapshotHash: latestResult.SnapshotPairResolution.NewSnapshotHash,
            ReadOnly: true,
            AllowDiffApply: false,
            AllowHistoryRestore: false,
            AllowRuntimeMutation: false,
            OpenMode: "ReadOnlySandbox",
            ManualButtonClick: true);

        var res = openHandler(req);
        lastRouteADetailOpenResult = res;
        RouteADetailOpenResultText = res.OpenSucceeded
            ? "RouteA detail viewer opened in read-only sandbox."
            : (res.FallbackToDryRun
                ? $"Open failed. Fallback to dry-run. {res.ErrorMessage}"
                : $"Open failed. {res.ErrorMessage}");

        if (!string.IsNullOrWhiteSpace(diagnosticsDirectory))
        {
            var refreshed = SceneAwareHistoryPreviewProbe.Run(diagnosticsDirectory, lastRouteADetailOpenResult);
            Apply(refreshed);
        }
    }

    private static string BuildMatchReasonText(SceneAwareHistoryPreviewItem item)
    {
        if (item.MatchReasons.Count == 0) return "Match reason: diagnostics unavailable";
        var lines = new List<string> { "Match reason:" };
        foreach (var reason in item.MatchReasons.Take(6)) lines.Add($"- {reason}");
        return string.Join(Environment.NewLine, lines);
    }
}
