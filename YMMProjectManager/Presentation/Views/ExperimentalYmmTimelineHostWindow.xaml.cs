using System.Windows;
using YMMProjectManager.Presentation.ViewModels;

namespace YMMProjectManager.Presentation.Views;

public partial class ExperimentalYmmTimelineHostWindow : Window
{
    public ExperimentalYmmTimelineHostWindow(ExperimentalYmmTimelineHostViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
