using System.Windows;

namespace YMMProjectManager.Presentation.Views;

public partial class ExperimentalYmmTimelineHostWindow : Window
{
    public ExperimentalYmmTimelineHostWindow(ExperimentalYmmTimelineHostViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
