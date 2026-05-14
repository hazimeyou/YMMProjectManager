namespace YMMProjectManager.Presentation.Timeline.Services;

public sealed record ReadonlyTimelineProjectionOptions(
    int ProjectionMarginFrames,
    int ProjectionMarginLayers,
    int HeavyProjectionCap,
    double MinimumVisualWidth,
    bool EnablePriorityProjection,
    bool EnableHeavyOptimization);

public sealed record ReadonlyTimelineProjectionRequest(
    IReadOnlyList<YMMProjectManager.Presentation.ViewModels.DiffTimelineItemViewModel> AllItems,
    string? SelectedItemId,
    int VisibleStartFrame,
    int VisibleEndFrame,
    int VisibleMinLayer,
    int VisibleMaxLayer,
    double ZoomLevel,
    bool ShowUnchangedItems,
    bool IsHeavyProject,
    ReadonlyTimelineProjectionOptions Options);

public sealed record ReadonlyTimelineProjectionDropCounts(
    int DropReasonViewport,
    int DropReasonLayer,
    int DropReasonFrame,
    int DropReasonCap);

public sealed record ReadonlyTimelineProjectionResult(
    IReadOnlyList<YMMProjectManager.Presentation.ViewModels.DiffTimelineItemViewModel> ProjectedItems,
    int ProjectedItemCount,
    int TotalItemCount,
    int DroppedItemCount,
    ReadonlyTimelineProjectionDropCounts DropCounts,
    int SuppressedTextCount,
    string OptimizationMode,
    string StatusText);

public interface IReadonlyTimelineProjectionService
{
    ReadonlyTimelineProjectionResult Project(ReadonlyTimelineProjectionRequest request);
}

public sealed class ReadonlyTimelineProjectionService : IReadonlyTimelineProjectionService
{
    public ReadonlyTimelineProjectionResult Project(ReadonlyTimelineProjectionRequest request)
    {
        var options = request.Options;
        var frameStart = Math.Max(0, request.VisibleStartFrame - options.ProjectionMarginFrames);
        var frameEnd = request.VisibleEndFrame + options.ProjectionMarginFrames;
        var layerStart = Math.Max(0, request.VisibleMinLayer - options.ProjectionMarginLayers);
        var layerEnd = request.VisibleMaxLayer + options.ProjectionMarginLayers;
        var viewportCenter = (request.VisibleStartFrame + request.VisibleEndFrame) / 2;

        var dropViewport = 0;
        var dropLayer = 0;
        var dropFrame = 0;
        var dropCap = 0;
        var suppressedText = 0;

        var candidates = new List<(YMMProjectManager.Presentation.ViewModels.DiffTimelineItemViewModel Item, int Priority)>();

        foreach (var item in request.AllItems)
        {
            var itemStart = item.Frame;
            var itemEnd = item.Frame + Math.Max(1, item.Length);
            var frameVisible = itemEnd >= frameStart && itemStart <= frameEnd;
            var layerVisible = item.Layer >= layerStart && item.Layer <= layerEnd;
            var unchangedVisible = request.ShowUnchangedItems || !item.IsUnchanged;

            if (!frameVisible)
            {
                dropFrame++;
                dropViewport++;
                continue;
            }

            if (!layerVisible)
            {
                dropLayer++;
                dropViewport++;
                continue;
            }

            if (!unchangedVisible)
            {
                continue;
            }

            var priority = 0;
            if (!string.IsNullOrWhiteSpace(request.SelectedItemId) && string.Equals(item.Id, request.SelectedItemId, StringComparison.Ordinal))
            {
                priority += 10000;
            }

            if (options.EnablePriorityProjection)
            {
                var distance = Math.Abs(item.Frame - viewportCenter);
                priority += Math.Max(0, 4000 - distance);
                priority += (itemStart >= request.VisibleStartFrame && itemEnd <= request.VisibleEndFrame) ? 500 : 100;
            }

            candidates.Add((item, priority));
        }

        var ordered = options.EnablePriorityProjection
            ? candidates.OrderByDescending(x => x.Priority).ThenBy(x => x.Item.Frame).ThenBy(x => x.Item.Layer)
            : candidates.OrderBy(x => x.Item.Frame).ThenBy(x => x.Item.Layer);

        var projected = new List<YMMProjectManager.Presentation.ViewModels.DiffTimelineItemViewModel>();
        foreach (var entry in ordered)
        {
            if (request.IsHeavyProject && options.EnableHeavyOptimization && projected.Count >= options.HeavyProjectionCap)
            {
                dropCap++;
                continue;
            }

            if (entry.Item.Width <= options.MinimumVisualWidth)
            {
                suppressedText++;
            }

            projected.Add(entry.Item);
        }

        var dropped = request.AllItems.Count - projected.Count;
        var mode = request.IsHeavyProject ? "Heavy" : "Standard";
        var status = request.IsHeavyProject
            ? "表示を最適化しています（一部アイテム表示を簡略化）"
            : "表示は安定モードです";

        return new ReadonlyTimelineProjectionResult(
            projected,
            projected.Count,
            request.AllItems.Count,
            dropped,
            new ReadonlyTimelineProjectionDropCounts(dropViewport, dropLayer, dropFrame, dropCap),
            suppressedText,
            mode,
            status);
    }
}
