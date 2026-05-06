namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmTimelineConstructorBindingResult
{
    public string TargetTypeName { get; set; } = string.Empty;

    public string ConstructorSignature { get; set; } = string.Empty;

    public IReadOnlyList<YmmTimelineConstructorParameterResult> Parameters { get; set; } = [];

    public bool AllRequiredParametersResolvable { get; set; }

    public bool CanAttemptGeneration { get; set; }

    public string? Notes { get; set; }
}
