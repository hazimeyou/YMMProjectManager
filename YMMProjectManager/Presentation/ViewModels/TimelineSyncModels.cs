namespace YMMProjectManager.Presentation.ViewModels;

public enum TimelineSyncState
{
    Unavailable,
    Detached,
    Synced,
    Manual,
    Error,
}

public enum TimelineMode
{
    Standalone,
    Synced,
    Comparison,
}
