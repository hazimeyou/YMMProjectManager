using System.Windows;

namespace YMMProjectManager.Presentation.Views;

public partial class ExperimentalYmmTimelineHostWindow : Window
{
    private ExperimentalYmmTimelineHostViewModel? Vm => DataContext as ExperimentalYmmTimelineHostViewModel;

    public ExperimentalYmmTimelineHostWindow(ExperimentalYmmTimelineHostViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnRedetectRuntimeClick(object sender, RoutedEventArgs e)
    {
        Vm?.RedetectRuntime();
    }

    private void OnRunProbeClick(object sender, RoutedEventArgs e)
    {
        var options = new PureTimelineExperimentalOptions
        {
            EnableExperimentalYmmTimelineHost = true,
            UseReflection = true,
            AllowViewModelGenerationAttempt = false,
            OpenIsolatedHostWindow = false,
        };
        Vm?.TryInitialize(options);
    }

    private void OnRunBindingDryRunClick(object sender, RoutedEventArgs e)
    {
        var options = new PureTimelineExperimentalOptions
        {
            EnableExperimentalYmmTimelineHost = true,
            UseReflection = true,
            AllowViewModelGenerationAttempt = false,
            OpenIsolatedHostWindow = false,
        };
        Vm?.TryInitialize(options);
    }

    private void OnRunGenerationAttemptClick(object sender, RoutedEventArgs e)
    {
        Vm?.TryRunGenerationAttempt();
    }

    private void OnSaveDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        Vm?.SaveDiagnosticsSnapshot();
    }
}
