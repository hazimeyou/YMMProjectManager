using System.Windows;
using YMMProjectManager.Presentation.Timeline.Experimental.ViewModels;

namespace YMMProjectManager.Presentation.Timeline.Experimental.Views;

public partial class SceneAwareHistoryPreviewInvestigationWindow : Window
{
    public SceneAwareHistoryPreviewInvestigationWindow(SceneAwareHistoryPreviewInvestigationViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
