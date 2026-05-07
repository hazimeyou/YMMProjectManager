namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmTimelineViewGenerationAttemptResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
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
    public bool DisposeAttempted { get; set; }
    public bool DisposeSucceeded { get; set; }
    public string? DisposeFailureReason { get; set; }
    public long GenerationAttemptMs { get; set; }
    public long DisposeMs { get; set; }
    public bool ExecutedOnStaThread { get; set; }
    public bool? WeakReferenceAliveAfterGc { get; set; }
}
