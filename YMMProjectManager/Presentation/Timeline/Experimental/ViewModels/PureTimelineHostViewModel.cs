namespace YMMProjectManager.Presentation.ViewModels;

public sealed class PureTimelineHostViewModel : ViewModelBase, IDisposable
{
    private readonly PureTimelineExperimentalOptions options;
    private IPureTimelineAdapter adapter;
    private PlaceholderPureTimelineAdapter? fallbackAdapter;
    private PureTimelineAdapterKind adapterKind;
    private string adapterDisplayName = string.Empty;
    private bool fallbackActive;
    private string? lastError;
    private PureTimelineStatus status = PureTimelineStatus.Unavailable;
    private string displayName = string.Empty;
    private string lastAction = "最終同期: (なし)";
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

    public PureTimelineAdapterKind AdapterKind
    {
        get => adapterKind;
        private set => SetProperty(ref adapterKind, value);
    }

    public string AdapterDisplayName
    {
        get => adapterDisplayName;
        private set => SetProperty(ref adapterDisplayName, value);
    }

    public bool FallbackActive
    {
        get => fallbackActive;
        private set => SetProperty(ref fallbackActive, value);
    }

    public string? LastError
    {
        get => lastError;
        private set => SetProperty(ref lastError, value);
    }

    public int InitializeCount => PureTimelineDiagnostics.InitializeCount;
    public int DisposeCount => PureTimelineDiagnostics.DisposeCount;
    public int DisposeFailureCount => PureTimelineDiagnostics.DisposeFailureCount;
    public int ActiveHostCount => PureTimelineDiagnostics.ActiveHostCount;
    public int ExperimentalReadyCount => PureTimelineDiagnostics.ExperimentalReadyCount;
    public int TimelineReflectionFailureCount => PureTimelineDiagnostics.TimelineReflectionFailureCount;

    public PureTimelineHostViewModel(PureTimelineAdapterKind kind, PureTimelineExperimentalOptions? options = null)
    {
        this.options = options ?? new PureTimelineExperimentalOptions();
        AdapterKind = kind;
        adapter = CreateAdapter(kind, this.options);
        UpdateAdapterProperties();
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
        UpdateAdapterProperties();
        LastAction = result.Succeeded
            ? $"最終同期: {result.Message}"
            : $"最終同期: 初期化失敗 - {result.Message}";
        if (!result.Succeeded)
        {
            LastError = result.Message;
            await ActivateFallbackAsync("Initialize failed fallback").ConfigureAwait(true);
        }
        else
        {
            LastError = null;
        }
        RefreshDiagnostics();
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
        if (!result.Succeeded)
        {
            LastError = result.Message;
        }

        LastAction = result.Succeeded
            ? $"最終同期: {result.Message}"
            : $"最終同期: フレーム設定失敗 - {result.Message}";
        RefreshDiagnostics();
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
        if (!result.Succeeded)
        {
            LastError = result.Message;
        }

        LastAction = result.Succeeded
            ? $"最終同期: {result.Message}"
            : $"最終同期: フレーム中央表示失敗 - {result.Message}";
        RefreshDiagnostics();
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
            ? $"最終同期: {result.Message}"
            : $"最終同期: 解放失敗 - {result.Message}";
        adapter.Dispose();
        RefreshDiagnostics();
    }

    public void SwitchAdapter(PureTimelineAdapterKind kind)
    {
        if (disposed || kind == AdapterKind)
        {
            return;
        }

        adapter.Dispose();
        fallbackAdapter?.Dispose();
        fallbackAdapter = null;
        FallbackActive = false;
        LastError = null;
        AdapterKind = kind;
        adapter = CreateAdapter(kind, options);
        UpdateAdapterProperties();
        InitializeAsync().GetAwaiter().GetResult();
        RefreshDiagnostics();
    }

    private async Task ActivateFallbackAsync(string reason)
    {
        fallbackAdapter ??= new PlaceholderPureTimelineAdapter();
        var init = await fallbackAdapter.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        if (!init.Succeeded)
        {
            Status = PureTimelineStatus.Error;
            LastAction = $"最終同期: フォールバック初期化失敗 - {init.Message}";
            return;
        }

        adapter = fallbackAdapter;
        FallbackActive = true;
        Status = adapter.Status;
        UpdateAdapterProperties();
        LastAction = $"最終同期: {reason}";
        RefreshDiagnostics();
    }

    private static IPureTimelineAdapter CreateAdapter(PureTimelineAdapterKind kind, PureTimelineExperimentalOptions options)
    {
        return kind switch
        {
            PureTimelineAdapterKind.FutureYmmTimeline => new FutureYmmTimelineAdapter(options),
            _ => new PlaceholderPureTimelineAdapter(),
        };
    }

    private void UpdateAdapterProperties()
    {
        DisplayName = adapter.DisplayName;
        AdapterDisplayName = adapter.DisplayName;
        IsAvailable = adapter.IsAvailable;
        Status = adapter.Status;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        DisposeAsync().GetAwaiter().GetResult();
    }

    private void RefreshDiagnostics()
    {
        OnPropertyChanged(nameof(InitializeCount));
        OnPropertyChanged(nameof(DisposeCount));
        OnPropertyChanged(nameof(DisposeFailureCount));
        OnPropertyChanged(nameof(ActiveHostCount));
        OnPropertyChanged(nameof(ExperimentalReadyCount));
        OnPropertyChanged(nameof(TimelineReflectionFailureCount));
    }
}
