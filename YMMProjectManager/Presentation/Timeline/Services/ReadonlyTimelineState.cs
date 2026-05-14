namespace YMMProjectManager.Presentation.Timeline.Services;

public sealed class ReadonlyTimelineViewportState
{
    public int VisibleStartFrame { get; private set; }
    public int VisibleEndFrame { get; private set; } = 1000;
    public int VisibleMinLayer { get; private set; }
    public int VisibleMaxLayer { get; private set; } = 50;
    public double ZoomLevel { get; private set; } = 0.1;
    public bool FitTimelineEnabled { get; private set; }
    public double ViewportWidth { get; private set; }
    public double ViewportHeight { get; private set; }

    public void UpdateVisibleFrameRange(int start, int end)
    {
        var normalizedStart = Math.Max(0, Math.Min(start, end));
        var normalizedEnd = Math.Max(normalizedStart, Math.Max(start, end));
        VisibleStartFrame = normalizedStart;
        VisibleEndFrame = normalizedEnd;
    }

    public void UpdateVisibleLayerRange(int minLayer, int maxLayer)
    {
        var normalizedMin = Math.Max(0, Math.Min(minLayer, maxLayer));
        var normalizedMax = Math.Max(normalizedMin, Math.Max(minLayer, maxLayer));
        VisibleMinLayer = normalizedMin;
        VisibleMaxLayer = normalizedMax;
    }

    public void UpdateZoom(double zoom) => ZoomLevel = Math.Max(0.02, zoom);
    public void UpdateFitMode(bool enabled) => FitTimelineEnabled = enabled;
    public void UpdateViewportSize(double width, double height)
    {
        ViewportWidth = Math.Max(0, width);
        ViewportHeight = Math.Max(0, height);
    }
}

public sealed class ReadonlyTimelineInteractionState
{
    public int? HoverFrame { get; private set; }
    public bool HoverGuideVisible { get; private set; }
    public DateTimeOffset? LastHoverUpdateUtc { get; private set; }
    public string? SelectedItemId { get; private set; }
    public int? SelectedFrame { get; private set; }
    public bool AutoScrollEnabled { get; private set; } = true;

    public void SetHoverFrame(int frame)
    {
        HoverFrame = Math.Max(0, frame);
        HoverGuideVisible = true;
        LastHoverUpdateUtc = DateTimeOffset.UtcNow;
    }

    public void ClearHover()
    {
        HoverFrame = null;
        HoverGuideVisible = false;
    }

    public void SetSelectedItem(string? itemId, int? frame)
    {
        SelectedItemId = itemId;
        SelectedFrame = frame;
    }

    public void SetAutoScrollEnabled(bool enabled) => AutoScrollEnabled = enabled;
}
