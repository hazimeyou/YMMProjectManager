namespace YMMProjectManager.Presentation.Timeline.Experimental;

internal interface IRouteBFeatureFlags
{
    bool EnableRouteBPreviewUi { get; }
    bool EnableRouteBReadonlyViewer { get; }
    bool EnableHeavyDiagnostics { get; }
    bool EnableAdvancedDiagnostics { get; }
    bool EnableExperimentalUi { get; }
}

internal sealed record RouteBFeatureConfiguration(
    bool EnableRouteBPreviewUi = false,
    bool EnableRouteBReadonlyViewer = false,
    bool EnableHeavyDiagnostics = false,
    bool EnableAdvancedDiagnostics = false,
    bool EnableExperimentalUi = false) : IRouteBFeatureFlags
{
    public static RouteBFeatureConfiguration Default { get; } = new();
}

