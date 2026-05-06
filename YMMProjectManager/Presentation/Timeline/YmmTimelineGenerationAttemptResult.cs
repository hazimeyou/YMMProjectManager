namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmTimelineGenerationAttemptResult
{
    public bool Attempted { get; set; }

    public bool Succeeded { get; set; }

    public string TargetTypeName { get; set; } = string.Empty;

    public string ConstructorSignature { get; set; } = string.Empty;

    public string? FailureReason { get; set; }

    public string? ExceptionType { get; set; }

    public string? ExceptionMessage { get; set; }

    public bool DisposeAttempted { get; set; }

    public bool DisposeSucceeded { get; set; }

    public string? DisposeFailureReason { get; set; }

    public long GenerationAttemptMs { get; set; }

    public long DisposeMs { get; set; }

    public IReadOnlyList<string> ConstructorParameters { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> NullInjectedParameters { get; set; } = Array.Empty<string>();

    public string? ExceptionStackTrace { get; set; }

    public bool GcVerificationAttempted { get; set; }

    public bool? WeakReferenceAliveAfterGc { get; set; }

    public string? FinalizationNote { get; set; }
}
