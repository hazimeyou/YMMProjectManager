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

    private async void OnRunProbeClick(object sender, RoutedEventArgs e)
    {
        var options = new PureTimelineExperimentalOptions
        {
            EnableExperimentalYmmTimelineHost = true,
            UseReflection = true,
            AllowViewModelGenerationAttempt = false,
            OpenIsolatedHostWindow = false,
        };
        await RunWithProgressAsync("Reflection Probe を実行しています...", () => Vm?.TryInitialize(options));
    }

    private async void OnRunBindingDryRunClick(object sender, RoutedEventArgs e)
    {
        var options = new PureTimelineExperimentalOptions
        {
            EnableExperimentalYmmTimelineHost = true,
            UseReflection = true,
            AllowViewModelGenerationAttempt = false,
            OpenIsolatedHostWindow = false,
        };
        await RunWithProgressAsync("Binding Dry-run を実行しています...", () => Vm?.TryInitialize(options));
    }

    private async void OnRunGenerationAttemptClick(object sender, RoutedEventArgs e)
    {
        await RunWithProgressAsync("生成試行(即破棄)を実行しています...", () => Vm?.TryRunGenerationAttempt());
    }

    private void OnSaveDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        Vm?.SaveDiagnosticsSnapshot();
    }

    private async Task RunWithProgressAsync(string message, Action action)
    {
        var popup = new ProgressPopupWindow
        {
            Owner = this,
            Message = message,
            Percent = 5,
        };

        try
        {
            popup.Show();
            popup.Percent = 15;
            await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

            popup.Percent = 35;
            await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

            action();

            popup.Percent = 90;
            await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

            popup.Percent = 100;
            await Task.Delay(80);
        }
        finally
        {
            popup.Close();
        }
    }
}
