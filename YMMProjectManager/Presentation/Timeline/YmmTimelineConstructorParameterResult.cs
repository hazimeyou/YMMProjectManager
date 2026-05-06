namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmTimelineConstructorParameterResult
{
    public string ParameterName { get; set; } = string.Empty;

    public string ParameterTypeName { get; set; } = string.Empty;

    public bool IsOptional { get; set; }

    public bool IsNullable { get; set; }

    public bool CanResolve { get; set; }

    public bool IsRequiredYmmRuntimeDependency { get; set; }

    public string? ResolutionSource { get; set; }

    public string? FailureReason { get; set; }
}
