
namespace YMMProjectManager.Presentation.Timeline;

public sealed class FutureYmmTimelineAdapter : IPureTimelineAdapter
{
    private readonly PureTimelineExperimentalOptions options;
    private ExperimentalYmmTimelineHostViewModel? experimentalHostViewModel;
    private ExperimentalYmmTimelineHostWindow? experimentalHostWindow;
    private bool disposed;
    private bool initialized;

    public PureTimelineStatus Status { get; private set; } = PureTimelineStatus.Unavailable;

    public string DisplayName => "YMM Timeline Adapter (Experimental)";

    public bool IsAvailable { get; private set; }

    public FutureYmmTimelineAdapter()
        : this(new PureTimelineExperimentalOptions())
    {
    }

    public FutureYmmTimelineAdapter(PureTimelineExperimentalOptions options)
    {
        this.options = options;
    }

    public Task<PureTimelineAdapterResult> InitializeAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(PureTimelineAdapterResult.Fail("Initialization canceled."));
        }

        if (disposed)
        {
            return Task.FromResult(PureTimelineAdapterResult.Fail("Adapter already disposed."));
        }

        PureTimelineDiagnostics.IncrementInitializeCount();

        if (!options.EnableExperimentalYmmTimelineHost)
        {
            Status = PureTimelineStatus.Unavailable;
            IsAvailable = false;
            return Task.FromResult(
                PureTimelineAdapterResult.Fail("Experimental YMM host disabled by default (preview12)."));
        }

        var sw = Stopwatch.StartNew();
        try
        {
            experimentalHostViewModel = new ExperimentalYmmTimelineHostViewModel();
            var ok = experimentalHostViewModel.TryInitialize(options.UseReflection);
            if (!ok)
            {
                Status = PureTimelineStatus.Unavailable;
                IsAvailable = false;
                PureTimelineDiagnostics.IncrementExperimentalYmmHostFailureCount();
                return Task.FromResult(PureTimelineAdapterResult.Fail(
                    $"Experimental host initialize failed: {experimentalHostViewModel.Summary}"));
            }

            if (options.OpenIsolatedHostWindow && System.Windows.Application.Current is not null)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    experimentalHostWindow = new ExperimentalYmmTimelineHostWindow(experimentalHostViewModel);
                    experimentalHostWindow.Show();
                });
                PureTimelineDiagnostics.IncrementActiveHostCount();
            }

            initialized = true;
            Status = PureTimelineStatus.ExperimentalReady;
            IsAvailable = true;
            PureTimelineDiagnostics.IncrementExperimentalYmmHostSuccessCount();
            PureTimelineDiagnostics.IncrementExperimentalReadyCount();
            sw.Stop();
            return Task.FromResult(PureTimelineAdapterResult.Ok($"Experimental host is ready in {sw.ElapsedMilliseconds} ms."));
        }
        catch (Exception ex)
        {
            Status = PureTimelineStatus.Error;
            IsAvailable = false;
            PureTimelineDiagnostics.IncrementExperimentalYmmHostFailureCount();
            return Task.FromResult(PureTimelineAdapterResult.Fail($"Experimental host exception: {ex.Message}", ex));
        }
    }

    public Task<PureTimelineAdapterResult> SetCurrentFrameAsync(int frame, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(PureTimelineAdapterResult.Fail("SetCurrentFrame canceled."));
        }

        if (!initialized)
        {
            return Task.FromResult(PureTimelineAdapterResult.Fail("YMM Timeline adapter is not initialized."));
        }

        return Task.FromResult(PureTimelineAdapterResult.Ok($"Set frame requested: {Math.Max(0, frame)}"));
    }

    public Task<PureTimelineAdapterResult> CenterFrameAsync(int frame, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(PureTimelineAdapterResult.Fail("CenterFrame canceled."));
        }

        if (!initialized)
        {
            return Task.FromResult(PureTimelineAdapterResult.Fail("YMM Timeline adapter is not initialized."));
        }

        return Task.FromResult(PureTimelineAdapterResult.Ok($"Center frame requested: {Math.Max(0, frame)}"));
    }

    public Task<PureTimelineAdapterResult> DisposeAsync()
    {
        PureTimelineDiagnostics.IncrementDisposeCount();
        Status = PureTimelineStatus.Unavailable;
        try
        {
            if (experimentalHostWindow is not null)
            {
                if (System.Windows.Application.Current is not null)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        experimentalHostWindow.Close();
                    });
                }

                experimentalHostWindow = null;
                PureTimelineDiagnostics.DecrementActiveHostCount();
            }

            experimentalHostViewModel?.Dispose();
            experimentalHostViewModel = null;
            disposed = true;
            initialized = false;
            Status = PureTimelineStatus.Detached;
            IsAvailable = false;
            return Task.FromResult(PureTimelineAdapterResult.Ok("Future YMM adapter disposed."));
        }
        catch (Exception ex)
        {
            PureTimelineDiagnostics.IncrementDisposeFailureCount();
            Status = PureTimelineStatus.Error;
            return Task.FromResult(PureTimelineAdapterResult.Fail($"Future YMM adapter dispose failed: {ex.Message}", ex));
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        DisposeAsync().GetAwaiter().GetResult();
    }
}
