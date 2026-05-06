namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmRuntimeDependencyDiscoveryOptions
{
    public int MaxDepth { get; set; } = 3;

    public int MaxNodes { get; set; } = 1200;

    public IReadOnlyList<string> ExcludedOwnerTypePrefixes { get; set; } =
    [
        "System.",
        "Microsoft.",
        "MS.Internal.",
        "WindowsBase",
        "PresentationCore",
        "PresentationFramework",
        "System.Windows."
    ];
}
