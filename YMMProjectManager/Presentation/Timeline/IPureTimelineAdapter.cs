namespace YMMProjectManager.Presentation.Timeline;

public interface IPureTimelineAdapter : IDisposable
{
    PureTimelineStatus Status { get; }

    string DisplayName { get; }

    bool IsAvailable { get; }

    Task<PureTimelineAdapterResult> InitializeAsync(CancellationToken cancellationToken);

    Task<PureTimelineAdapterResult> SetCurrentFrameAsync(int frame, CancellationToken cancellationToken);

    Task<PureTimelineAdapterResult> CenterFrameAsync(int frame, CancellationToken cancellationToken);

    Task<PureTimelineAdapterResult> DisposeAsync();
}
