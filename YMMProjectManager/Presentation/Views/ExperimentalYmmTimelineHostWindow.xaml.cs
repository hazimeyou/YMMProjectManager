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
        await RunWithProgressAsync("Reflection Probe を実行しています...", (progress) =>
        {
            return Vm?.TryInitializeAsync(options, progress) ?? Task.FromResult(false);
        });
    }

    private async void OnRunAllDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        Vm?.RedetectRuntime();
        await RunWithProgressAsync("再判定から保存までを実行しています...", async (progress) =>
        {
            var ok = await (Vm?.TryInitializeAsync(new PureTimelineExperimentalOptions
            {
                EnableExperimentalYmmTimelineHost = true,
                UseReflection = true,
                OpenIsolatedHostWindow = false,
                AllowViewModelGenerationAttempt = false,
            }, progress) ?? Task.FromResult(false));

            Vm?.SaveDiagnosticsSnapshot();
            return ok;
        });
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
        await RunWithProgressAsync("Binding Dry-run を実行しています...", (progress) =>
        {
            return Vm?.TryInitializeAsync(options, progress) ?? Task.FromResult(false);
        });
    }

    private async void OnRunGenerationAttemptClick(object sender, RoutedEventArgs e)
    {
        await RunWithProgressAsync("生成試行(即破棄)を実行しています...", (progress) =>
        {
            return Vm?.TryInitializeAsync(new PureTimelineExperimentalOptions
            {
                EnableExperimentalYmmTimelineHost = true,
                UseReflection = true,
                OpenIsolatedHostWindow = false,
                AllowViewModelGenerationAttempt = true,
                AllowTimelineViewGenerationAttempt = true,
                MinimumReadinessScoreForGeneration = 80,
                DisposeImmediatelyAfterGeneration = true,
                ForbidVisualTreeAttach = false,
                AllowPassiveVisualTreeParticipation = true,
            }, progress) ?? Task.FromResult(false);
        });
    }

    private void OnSaveDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        Vm?.SaveDiagnosticsSnapshot();
    }

    private void OnCopyReflectionClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Clipboard.SetText(Vm.BuildReflectionClipboardText());
    }

    private void OnCopyBindingClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Clipboard.SetText(Vm.BuildBindingClipboardText());
    }

    private void OnCopyGenerationClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Clipboard.SetText(Vm.BuildGenerationClipboardText());
    }

    private void OnCopyDisposeClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Clipboard.SetText(Vm.BuildDisposeClipboardText());
    }

    private void OnCopyLogsClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Clipboard.SetText(Vm.BuildLogsClipboardText());
    }

    private async Task RunWithProgressAsync(string message, Func<IProgress<int>, Task<bool>> workAsync)
    {
        var popup = new ProgressPopupWindow
        {
            Owner = this,
            Message = message,
            Percent = 5,
        };
        var progress = new Progress<int>(v => popup.Percent = Math.Max(0, Math.Min(100, v)));

        try
        {
            popup.Show();
            await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            popup.Percent = 10;
            await workAsync(progress);
            popup.Percent = 100;
            await Task.Delay(80);
        }
        finally
        {
            popup.Close();
        }
    }
}
