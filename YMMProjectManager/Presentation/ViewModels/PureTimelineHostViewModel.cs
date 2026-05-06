using YMMProjectManager.Presentation.Timeline;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed class PureTimelineHostViewModel : ViewModelBase, IDisposable
{
    private readonly IPureTimelineAdapter adapter;
    private PureTimelineStatus status = PureTimelineStatus.Unavailable;
    private string displayName = string.Empty;
    private string lastAction = "Last Sync: (none)";
    private bool isAvailable;
    private int currentFrame;
    private bool disposed;

    public PureTimelineStatus Status
    {
        get => status;
        private set => SetProperty(ref status, value);
    }

    public string DisplayName
    {
        get => displayName;
        private set => SetProperty(ref displayName, value);
    }

    public string LastAction
    {
        get => lastAction;
        private set => SetProperty(ref lastAction, value);
    }

    public bool IsAvailable
    {
        get => isAvailable;
        private set => SetProperty(ref isAvailable, value);
    }

    public int CurrentFrame
    {
        get => currentFrame;
        set => SetProperty(ref currentFrame, Math.Max(0, value));
    }

    public PureTimelineHostViewModel(IPureTimelineAdapter adapter)
    {
        this.adapter = adapter;
        DisplayName = adapter.DisplayName;
        IsAvailable = adapter.IsAvailable;
        Status = adapter.Status;
    }

    public async Task InitializeAsync()
    {
        if (disposed)
        {
            return;
        }

        Status = PureTimelineStatus.Initializing;
        var result = await adapter.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        Status = adapter.Status;
        LastAction = result.Succeeded
            ? $"Last Sync: {result.Message}"
            : $"Last Sync: Initialize failed - {result.Message}";
        if (!result.Succeeded)
        {
            Status = PureTimelineStatus.Error;
        }
    }

    public async Task SetCurrentFrameAsync(int frame)
    {
        if (disposed)
        {
            return;
        }

        var normalizedFrame = Math.Max(0, frame);
        CurrentFrame = normalizedFrame;
        var result = await adapter.SetCurrentFrameAsync(normalizedFrame, CancellationToken.None).ConfigureAwait(true);
        Status = result.Succeeded ? adapter.Status : PureTimelineStatus.Error;
        LastAction = result.Succeeded
            ? $"Last Sync: {result.Message}"
            : $"Last Sync: Set frame failed - {result.Message}";
    }

    public async Task CenterFrameAsync(int frame)
    {
        if (disposed)
        {
            return;
        }

        var normalizedFrame = Math.Max(0, frame);
        CurrentFrame = normalizedFrame;
        var result = await adapter.CenterFrameAsync(normalizedFrame, CancellationToken.None).ConfigureAwait(true);
        Status = result.Succeeded ? adapter.Status : PureTimelineStatus.Error;
        LastAction = result.Succeeded
            ? $"Last Sync: {result.Message}"
            : $"Last Sync: Center frame failed - {result.Message}";
    }

    public async Task DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        var result = await adapter.DisposeAsync().ConfigureAwait(true);
        Status = result.Succeeded ? PureTimelineStatus.Detached : PureTimelineStatus.Error;
        LastAction = result.Succeeded
            ? $"Last Sync: {result.Message}"
            : $"Last Sync: Dispose failed - {result.Message}";
        adapter.Dispose();
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
