namespace YMMProjectManager.Presentation.Timeline;

public sealed class PlaceholderPureTimelineAdapter : IPureTimelineAdapter
{
    private bool disposed;
    private int currentFrame;

    public PureTimelineStatus Status { get; private set; } = PureTimelineStatus.Unavailable;

    public string DisplayName => "Placeholder Adapter";

    public bool IsAvailable => true;

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

        Status = PureTimelineStatus.Placeholder;
        return Task.FromResult(PureTimelineAdapterResult.Ok("Placeholder adapter initialized."));
    }

    public Task<PureTimelineAdapterResult> SetCurrentFrameAsync(int frame, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(PureTimelineAdapterResult.Fail("SetCurrentFrame canceled."));
        }

        if (disposed)
        {
            return Task.FromResult(PureTimelineAdapterResult.Fail("Adapter already disposed."));
        }

        currentFrame = Math.Max(0, frame);
        return Task.FromResult(PureTimelineAdapterResult.Ok($"Set frame to {currentFrame}."));
    }

    public Task<PureTimelineAdapterResult> CenterFrameAsync(int frame, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(PureTimelineAdapterResult.Fail("CenterFrame canceled."));
        }

        if (disposed)
        {
            return Task.FromResult(PureTimelineAdapterResult.Fail("Adapter already disposed."));
        }

        currentFrame = Math.Max(0, frame);
        return Task.FromResult(PureTimelineAdapterResult.Ok($"Centered frame at {currentFrame}."));
    }

    public Task<PureTimelineAdapterResult> DisposeAsync()
    {
        disposed = true;
        Status = PureTimelineStatus.Detached;
        return Task.FromResult(PureTimelineAdapterResult.Ok("Placeholder adapter disposed."));
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
