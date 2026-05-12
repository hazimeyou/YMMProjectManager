namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectDiffViewModel
{
    private void NotifyDiagnosticsChanged()
    {
        OnPropertyChanged(nameof(HasVirtualizationWarning));
        OnPropertyChanged(nameof(VirtualizationRecommendationText));
        OnPropertyChanged(nameof(CompactRenderDiagnosticsText));
        OnPropertyChanged(nameof(DiagnosticsDetailsText));
    }
}
