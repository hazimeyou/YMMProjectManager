namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectDiffViewModel
{
    public void LoadMoreRows()
    {
        if (!CanLoadMoreRows || latestCoreResult is null)
        {
            return;
        }

        visibleRowWindowSize += Math.Max(250, materializedRowLimit / 2);
        MaterializeRowsForCurrentWindow(latestCoreResult);
        OnRowWindowStateChanged();
    }

    public void ResetRowWindow()
    {
        visibleRowWindowStart = 0;
        visibleRowWindowSize = 500;
        if (latestCoreResult is not null)
        {
            MaterializeRowsForCurrentWindow(latestCoreResult);
        }
        OnRowWindowStateChanged();
    }

    private void OnRowWindowStateChanged()
    {
        OnPropertyChanged(nameof(IsLargeResultMode));
        OnPropertyChanged(nameof(LargeResultModeReason));
        OnPropertyChanged(nameof(MaterializedRowLimit));
        OnPropertyChanged(nameof(TotalAvailableRowCount));
        OnPropertyChanged(nameof(DisplayedRowCount));
        OnPropertyChanged(nameof(DeferredRowCount));
        OnPropertyChanged(nameof(VisibleRowWindowStart));
        OnPropertyChanged(nameof(VisibleRowWindowSize));
        OnPropertyChanged(nameof(CanLoadMoreRows));
        OnPropertyChanged(nameof(RowWindowSummaryText));
        OnPropertyChanged(nameof(DiagnosticsDetailsText));
        OnPropertyChanged(nameof(CompactRenderDiagnosticsText));
    }
}
