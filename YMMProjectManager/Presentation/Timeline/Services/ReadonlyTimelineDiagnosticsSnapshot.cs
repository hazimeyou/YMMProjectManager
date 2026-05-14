namespace YMMProjectManager.Presentation.Timeline.Services;

public sealed record ReadonlyTimelineProjectionDiagnostics(
    int ProjectedItemCount,
    int TotalItemCount,
    int DroppedItemCount,
    int HeavyProjectionDropCount,
    int SuppressedTextCount,
    string OptimizationMode,
    int ProjectionMarginFrames,
    int ProjectionMarginLayers,
    int ProjectionCap);

public sealed record ReadonlyTimelineViewportDiagnostics(
    int VisibleStartFrame,
    int VisibleEndFrame,
    int VisibleLayerStart,
    int VisibleLayerEnd,
    double ZoomLevel,
    bool FitTimelineEnabled,
    double ViewportWidth,
    double ViewportHeight);

public sealed record ReadonlyTimelineInteractionDiagnostics(
    int? HoverFrame,
    bool HoverGuideVisible,
    string? SelectedItemId,
    int? SelectedFrame,
    bool AutoScrollEnabled);

public sealed record ReadonlyTimelineSafetyDiagnostics(
    bool ReadOnly,
    bool ManualOnly,
    bool ProductionFeature);

public sealed record ReadonlyTimelineDisplayDiagnostics(
    string DisplayCountText,
    string OptimizationStatusText,
    string ReadonlyStatusText);

public sealed record ReadonlyTimelineDiagnosticsSnapshot(
    DateTimeOffset Timestamp,
    ReadonlyTimelineProjectionDiagnostics Projection,
    ReadonlyTimelineViewportDiagnostics Viewport,
    ReadonlyTimelineInteractionDiagnostics Interaction,
    ReadonlyTimelineSafetyDiagnostics Safety,
    ReadonlyTimelineDisplayDiagnostics Display);

public sealed class ReadonlyTimelineDiagnosticsSnapshotBuilder
{
    public ReadonlyTimelineDiagnosticsSnapshot Build(
        ReadonlyTimelineProjectionResult projectionResult,
        ReadonlyTimelineProjectionOptions options,
        ReadonlyTimelineViewportState viewportState,
        ReadonlyTimelineInteractionState interactionState,
        bool readOnly,
        bool manualOnly,
        bool productionFeature)
    {
        var projection = new ReadonlyTimelineProjectionDiagnostics(
            projectionResult.ProjectedItemCount,
            projectionResult.TotalItemCount,
            projectionResult.DroppedItemCount,
            projectionResult.DropCounts.DropReasonCap,
            projectionResult.SuppressedTextCount,
            projectionResult.OptimizationMode,
            options.ProjectionMarginFrames,
            options.ProjectionMarginLayers,
            options.HeavyProjectionCap);

        var viewport = new ReadonlyTimelineViewportDiagnostics(
            viewportState.VisibleStartFrame,
            viewportState.VisibleEndFrame,
            viewportState.VisibleMinLayer,
            viewportState.VisibleMaxLayer,
            viewportState.ZoomLevel,
            viewportState.FitTimelineEnabled,
            viewportState.ViewportWidth,
            viewportState.ViewportHeight);

        var interaction = new ReadonlyTimelineInteractionDiagnostics(
            interactionState.HoverFrame,
            interactionState.HoverGuideVisible,
            interactionState.SelectedItemId,
            interactionState.SelectedFrame,
            interactionState.AutoScrollEnabled);

        var safety = new ReadonlyTimelineSafetyDiagnostics(readOnly, manualOnly, productionFeature);
        var display = new ReadonlyTimelineDisplayDiagnostics(
            $"{projectionResult.ProjectedItemCount} / {projectionResult.TotalItemCount}",
            projectionResult.StatusText,
            readOnly ? "読み取り専用" : "編集可能");

        return new ReadonlyTimelineDiagnosticsSnapshot(
            DateTimeOffset.UtcNow,
            projection,
            viewport,
            interaction,
            safety,
            display);
    }
}
