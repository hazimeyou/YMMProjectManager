using System.Windows;
using YMMProjectManager.Presentation.ViewModels;

namespace YMMProjectManager.Presentation.Views;

public partial class ProjectDiffWindow : Window
{
    public ProjectDiffWindow(ProjectDiffViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
