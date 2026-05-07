namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmTimelineViewGenerationAttemptResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public bool HostCreated { get; set; }
    public bool HostShownOrInitialized { get; set; }
    public bool ViewAttachedToHost { get; set; }
    public string TargetTypeName { get; set; } = string.Empty;
    public string ConstructorSignature { get; set; } = string.Empty;
    public string? FailureReason { get; set; }
    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }
    public bool VisualAttachAttempted { get; set; }
    public bool VisualAttachForbidden { get; set; }
    public bool VisualAttachSucceeded { get; set; }
    public bool DetachSucceeded { get; set; }
    public long AttachDurationMs { get; set; }
    public bool DataContextAssigned { get; set; }
    public bool LoadedEventObserved { get; set; }
    public bool InitializedEventObserved { get; set; }
    public bool DataContextChangedObserved { get; set; }
    public bool TemplateAppliedObserved { get; set; }
    public bool LayoutUpdatedObserved { get; set; }
    public bool RenderingObserved { get; set; }
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public string DesiredSize { get; set; } = string.Empty;
    public string RenderSize { get; set; } = string.Empty;
    public bool IsVisible { get; set; }
    public bool IsLoaded { get; set; }
    public bool PresentationSourceAvailable { get; set; }
    public bool DispatcherRenderPriorityReached { get; set; }
    public bool MinimalRenderObserved { get; set; }
    public bool DisposeAttempted { get; set; }
    public bool DisposeSucceeded { get; set; }
    public string? DisposeFailureReason { get; set; }
    public long GenerationAttemptMs { get; set; }
    public long DisposeMs { get; set; }
    public bool ExecutedOnStaThread { get; set; }
    public bool? WeakReferenceAliveAfterGc { get; set; }
    public bool BindingErrorObservationUnavailable { get; set; } = true;
    public IReadOnlyList<YmmTimelineDataContextBoundaryPatternResult> DataContextBoundaryPatterns { get; set; } = [];
    public YmmTimelinePassiveEventBoundaryResult? PassiveEventBoundary { get; set; }
    public YmmTimelineCommandRouteBoundaryResult? CommandRouteBoundary { get; set; }
}

public sealed class YmmTimelineDataContextBoundaryPatternResult
{
    public string Name { get; set; } = string.Empty;
    public bool Attempted { get; set; }
    public string SkippedReason { get; set; } = string.Empty;
    public string DataContextType { get; set; } = string.Empty;
    public bool GeneratedViewModelAvailable { get; set; }
    public string GeneratedViewModelTypeName { get; set; } = string.Empty;
    public bool AttachSucceeded { get; set; }
    public bool PresentationSourceAvailable { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsVisible { get; set; }
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public string DesiredSize { get; set; } = string.Empty;
    public string RenderSize { get; set; } = string.Empty;
    public bool DispatcherRenderPriorityReached { get; set; }
    public bool RenderingObserved { get; set; }
    public bool TemplateAppliedObserved { get; set; }
    public int BindingErrorCount { get; set; }
    public int ExceptionCount { get; set; }
    public IReadOnlyList<string> ExceptionTypes { get; set; } = [];
    public bool MinimalRenderObserved { get; set; }
    public bool DetachSucceeded { get; set; }
    public bool DisposeSucceeded { get; set; }
    public bool PatternDisposeManagedByOuterScope { get; set; }
}

public sealed class YmmTimelinePassiveEventBoundaryResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public string SkippedReason { get; set; } = string.Empty;
    public bool HostCreated { get; set; }
    public bool ViewAttachedToHost { get; set; }
    public bool GeneratedViewModelAvailable { get; set; }
    public string GeneratedViewModelTypeName { get; set; } = string.Empty;
    public bool PresentationSourceAvailable { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsVisible { get; set; }
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public string DesiredSize { get; set; } = string.Empty;
    public string RenderSize { get; set; } = string.Empty;
    public bool DispatcherLoadedPriorityReached { get; set; }
    public bool DispatcherRenderPriorityReached { get; set; }
    public bool RenderingObserved { get; set; }
    public bool TemplateAppliedObserved { get; set; }
    public IReadOnlyList<string> ObservedEvents { get; set; } = [];
    public IReadOnlyDictionary<string, int> EventCounts { get; set; } = new Dictionary<string, int>();
    public string FirstEventName { get; set; } = string.Empty;
    public string LastEventName { get; set; } = string.Empty;
    public int ExceptionCount { get; set; }
    public IReadOnlyList<string> ExceptionTypes { get; set; } = [];
    public bool DetachSucceeded { get; set; }
    public bool DisposeSucceeded { get; set; }
    public bool FallbackPreserved { get; set; } = true;
}

public sealed class YmmTimelineCommandRouteBoundaryResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public string SkippedReason { get; set; } = string.Empty;
    public bool HostCreated { get; set; }
    public bool ViewAttachedToHost { get; set; }
    public bool GeneratedViewModelAvailable { get; set; }
    public bool PresentationSourceAvailable { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsVisible { get; set; }
    public bool CommandInfrastructureObserved { get; set; }
    public int InputBindingCount { get; set; }
    public int CommandBindingCount { get; set; }
    public int RoutedCommandCount { get; set; }
    public int CommandSourceCount { get; set; }
    public bool Focusable { get; set; }
    public bool IsKeyboardFocusWithin { get; set; }
    public string FocusScopeType { get; set; } = string.Empty;
    public bool TraversalRequestAvailable { get; set; }
    public bool KeyboardNavigationObserved { get; set; }
    public bool ContextMenuPresent { get; set; }
    public bool ToolTipPresent { get; set; }
    public int ExceptionCount { get; set; }
    public IReadOnlyList<string> ExceptionTypes { get; set; } = [];
    public bool DetachSucceeded { get; set; }
    public bool DisposeSucceeded { get; set; }
    public bool FallbackPreserved { get; set; } = true;
}
