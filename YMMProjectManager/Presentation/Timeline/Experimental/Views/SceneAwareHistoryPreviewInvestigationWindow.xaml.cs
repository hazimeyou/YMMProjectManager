using System.Windows;
using YMMProjectManager.Presentation.Timeline.Experimental;
using YMMProjectManager.Presentation.Timeline.Experimental.ViewModels;

namespace YMMProjectManager.Presentation.Timeline.Experimental.Views;

public partial class SceneAwareHistoryPreviewInvestigationWindow : Window
{
    public SceneAwareHistoryPreviewInvestigationWindow(SceneAwareHistoryPreviewInvestigationViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void OpenRouteADetailPreview_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SceneAwareHistoryPreviewInvestigationViewModel vm)
        {
            vm.OpenRouteADetailPreviewDryRun();
        }
    }

    private void RerunInvestigation_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SceneAwareHistoryPreviewInvestigationViewModel vm)
        {
            return;
        }

        var diagnosticsDir = Path.Combine(AppContext.BaseDirectory, "diagnostics");
        var probeResult = SceneAwareHistoryPreviewProbe.Run(diagnosticsDir);
        vm.Apply(probeResult);
    }
}
