namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmRuntimeDependencyCandidate
{
    public string DependencyName { get; set; } = string.Empty;

    public string TargetTypeName { get; set; } = string.Empty;

    public string OwnerTypeName { get; set; } = string.Empty;

    public string MemberKind { get; set; } = string.Empty;

    public string MemberName { get; set; } = string.Empty;

    public bool IsStatic { get; set; }

    public bool CanReadExistingInstance { get; set; }

    public bool ExistingInstanceFound { get; set; }

    public string? AccessError { get; set; }

    public string? RoutePath { get; set; }

    public int Depth { get; set; }
}
