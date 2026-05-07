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
    public YmmTimelineVisualTreeInventoryResult? VisualTreeInventory { get; set; }
    public YmmTimelineBindingSurfaceInventoryResult? BindingSurfaceInventory { get; set; }
    public YmmTimelineResourceInventoryResult? ResourceInventory { get; set; }
    public YmmTimelineLifecycleRepeatabilityResult? LifecycleRepeatability { get; set; }
    public YmmTimelineExpandedVisualTreeInventoryResult? ExpandedVisualTreeInventory { get; set; }
    public YmmTimelineLayoutSizeSweepResult? LayoutSizeSweep { get; set; }
    public YmmTimelineDispatcherPriorityBoundaryResult? DispatcherPriorityBoundary { get; set; }
    public YmmTimelineScrollContentInventoryResult? ScrollContentInventory { get; set; }
    public YmmTimelineViewModelSurfaceInventoryResult? ViewModelSurfaceInventory { get; set; }
    public YmmTimelineThemeResourceSmokeResult? ThemeResourceSmoke { get; set; }
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

public sealed class YmmTimelineVisualTreeInventoryResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public bool HostCreated { get; set; }
    public bool ViewAttachedToHost { get; set; }
    public bool GeneratedViewModelAvailable { get; set; }
    public bool PresentationSourceAvailable { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsVisible { get; set; }
    public int VisualTreeNodeCount { get; set; }
    public int MaxDepth { get; set; }
    public int CommandSourceCount { get; set; }
    public int ExceptionCount { get; set; }
    public IReadOnlyList<string> ExceptionTypes { get; set; } = [];
    public bool DetachSucceeded { get; set; }
    public bool DisposeSucceeded { get; set; }
    public bool FallbackPreserved { get; set; } = true;
    public IReadOnlyList<YmmTimelineVisualNodeInfo> Nodes { get; set; } = [];
}
public sealed class YmmTimelineVisualNodeInfo
{
    public int Depth { get; set; }
    public string TypeName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string AutomationId { get; set; } = string.Empty;
    public bool IsVisible { get; set; }
    public bool IsEnabled { get; set; }
    public bool Focusable { get; set; }
    public bool IsKeyboardFocusWithin { get; set; }
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public string CommandSourceTypeName { get; set; } = string.Empty;
    public string CommandTypeName { get; set; } = string.Empty;
    public string CommandName { get; set; } = string.Empty;
    public string CommandParameterTypeName { get; set; } = string.Empty;
    public string CommandTargetTypeName { get; set; } = string.Empty;
    public bool CommandCanExecuteObservationSkipped { get; set; } = true;
}

public sealed class YmmTimelineBindingSurfaceInventoryResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public bool BindingObservationAvailable { get; set; } = true;
    public int BindingExpressionCount { get; set; }
    public int UnresolvedBindingCount { get; set; }
    public int BindingErrorCount { get; set; }
    public int DependencyPropertySampleCount { get; set; }
    public int ExceptionCount { get; set; }
    public IReadOnlyList<string> ExceptionTypes { get; set; } = [];
    public bool DetachSucceeded { get; set; }
    public bool DisposeSucceeded { get; set; }
    public bool FallbackPreserved { get; set; } = true;
}

public sealed class YmmTimelineResourceInventoryResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public int ResourceDictionaryCount { get; set; }
    public bool ApplicationResourceAvailable { get; set; }
    public int HostResourceCount { get; set; }
    public int ViewResourceCount { get; set; }
    public int StyleObservedCount { get; set; }
    public int ControlTemplateObservedCount { get; set; }
    public int DataTemplateObservedCount { get; set; }
    public int MissingResourceSignsCount { get; set; }
    public int ExceptionCount { get; set; }
    public IReadOnlyList<string> ExceptionTypes { get; set; } = [];
    public bool DetachSucceeded { get; set; }
    public bool DisposeSucceeded { get; set; }
    public bool FallbackPreserved { get; set; } = true;
}

public sealed class YmmTimelineLifecycleRepeatabilityResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public int IterationCount { get; set; }
    public int SucceededCount { get; set; }
    public int FailedCount { get; set; }
    public int TotalExceptionCount { get; set; }
    public bool FallbackPreserved { get; set; } = true;
    public bool FinalDisposeSucceeded { get; set; }
    public IReadOnlyList<YmmTimelineLifecycleIterationResult> Iterations { get; set; } = [];
}
public sealed class YmmTimelineLifecycleIterationResult
{
    public int Index { get; set; }
    public bool HostCreated { get; set; }
    public bool ViewAttachedToHost { get; set; }
    public bool PresentationSourceAvailable { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsVisible { get; set; }
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public bool RenderingObserved { get; set; }
    public bool TemplateAppliedObserved { get; set; }
    public bool DetachSucceeded { get; set; }
    public bool DisposeSucceeded { get; set; }
    public int ExceptionCount { get; set; }
    public IReadOnlyList<string> ExceptionTypes { get; set; } = [];
    public bool? WeakReferenceAliveBeforeGc { get; set; }
    public bool? WeakReferenceAliveAfterGc { get; set; }
    public bool GcAttempted { get; set; }
}

public sealed class YmmTimelineExpandedVisualTreeInventoryResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public int MaxVisualNodes { get; set; } = 1000;
    public int VisualTreeNodeCount { get; set; }
    public int MaxDepth { get; set; }
    public IReadOnlyDictionary<string, int> TypeHistogram { get; set; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<int, int> DepthHistogram { get; set; } = new Dictionary<int, int>();
    public IReadOnlyDictionary<string, int> CommandSourceTypeHistogram { get; set; } = new Dictionary<string, int>();
    public int ControlTypeCount { get; set; }
    public int PanelTypeCount { get; set; }
    public int ItemsControlCount { get; set; }
    public int ScrollViewerCount { get; set; }
    public int TextBlockCount { get; set; }
    public int ButtonLikeCount { get; set; }
}

public sealed class YmmTimelineLayoutSizeSweepResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public IReadOnlyList<YmmTimelineLayoutSizeSweepEntry> Entries { get; set; } = [];
}
public sealed class YmmTimelineLayoutSizeSweepEntry
{
    public string Size { get; set; } = string.Empty;
    public bool PresentationSourceAvailable { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsVisible { get; set; }
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public string DesiredSize { get; set; } = string.Empty;
    public string RenderSize { get; set; } = string.Empty;
    public int VisualTreeNodeCount { get; set; }
    public int BindingErrorCount { get; set; }
    public int ExceptionCount { get; set; }
    public bool RenderingObserved { get; set; }
    public bool TemplateAppliedObserved { get; set; }
    public bool DetachSucceeded { get; set; }
    public bool DisposeSucceeded { get; set; }
}

public sealed class YmmTimelineDispatcherPriorityBoundaryResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public IReadOnlyList<YmmTimelineDispatcherPriorityEntry> Entries { get; set; } = [];
}
public sealed class YmmTimelineDispatcherPriorityEntry
{
    public string PriorityName { get; set; } = string.Empty;
    public bool Reached { get; set; }
    public bool PresentationSourceAvailable { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsVisible { get; set; }
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public string DesiredSize { get; set; } = string.Empty;
    public string RenderSize { get; set; } = string.Empty;
    public int BindingExpressionCount { get; set; }
    public int BindingErrorCount { get; set; }
    public int ObservedEventCount { get; set; }
    public int ExceptionCount { get; set; }
}

public sealed class YmmTimelineScrollContentInventoryResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public IReadOnlyDictionary<string, int> TypeCounts { get; set; } = new Dictionary<string, int>();
}

public sealed class YmmTimelineViewModelSurfaceInventoryResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public string ViewModelTypeName { get; set; } = string.Empty;
    public int PublicPropertyCount { get; set; }
    public int PublicMethodCount { get; set; }
    public int PublicCommandLikePropertyCount { get; set; }
    public int CollectionLikePropertyCount { get; set; }
    public int ObservablePropertyCount { get; set; }
    public IReadOnlyDictionary<string, int> PropertyTypeHistogram { get; set; } = new Dictionary<string, int>();
    public IReadOnlyList<string> ICommandPropertyNames { get; set; } = [];
    public IReadOnlyList<string> CollectionPropertyNames { get; set; } = [];
    public bool ImplementsINotifyPropertyChanged { get; set; }
    public bool ImplementsIDisposable { get; set; }
}

public sealed class YmmTimelineThemeResourceSmokeResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public bool ApplicationCurrentExists { get; set; }
    public int MergedDictionariesCount { get; set; }
    public IReadOnlyList<string> ApplicationResourceKeys { get; set; } = [];
    public IReadOnlyList<string> ViewResourceKeys { get; set; } = [];
    public IReadOnlyList<string> HostResourceKeys { get; set; } = [];
    public int MissingResourceSignsCount { get; set; }
}
