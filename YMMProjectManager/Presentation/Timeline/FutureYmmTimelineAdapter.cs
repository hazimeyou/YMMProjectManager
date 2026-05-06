namespace YMMProjectManager.Presentation.Timeline;

public sealed class FutureYmmTimelineAdapter : IPureTimelineAdapter
{
    private bool disposed;

    public PureTimelineStatus Status { get; private set; } = PureTimelineStatus.Unavailable;

    public string DisplayName => "YMM Timeline Adapter (Experimental)";

    public bool IsAvailable { get; private set; }

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

        Status = PureTimelineStatus.Unavailable;
        IsAvailable = false;
        return Task.FromResult(
            PureTimelineAdapterResult.Fail("YMM Timeline adapter is not available in preview11."));
    }

    public Task<PureTimelineAdapterResult> SetCurrentFrameAsync(int frame, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(PureTimelineAdapterResult.Fail("SetCurrentFrame canceled."));
        }

        return Task.FromResult(PureTimelineAdapterResult.Fail("YMM Timeline adapter is not initialized."));
    }

    public Task<PureTimelineAdapterResult> CenterFrameAsync(int frame, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(PureTimelineAdapterResult.Fail("CenterFrame canceled."));
        }

        return Task.FromResult(PureTimelineAdapterResult.Fail("YMM Timeline adapter is not initialized."));
    }

    public Task<PureTimelineAdapterResult> DisposeAsync()
    {
        disposed = true;
        Status = PureTimelineStatus.Detached;
        return Task.FromResult(PureTimelineAdapterResult.Ok("Future YMM adapter disposed."));
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
