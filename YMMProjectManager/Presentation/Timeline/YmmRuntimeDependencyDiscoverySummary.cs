namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmRuntimeDependencyDiscoverySummary
{
    public string DependencyType { get; set; } = string.Empty;

    public int CandidateCount { get; set; }

    public bool Resolved { get; set; }

    public IReadOnlyList<string> ResolutionAttempts { get; set; } = [];

    public IReadOnlyList<string> CandidateOwners { get; set; } = [];

    public IReadOnlyList<string> StaticProperties { get; set; } = [];

    public IReadOnlyList<string> InstanceFields { get; set; } = [];

    public IReadOnlyList<string> ServiceProviders { get; set; } = [];

    public string Confidence { get; set; } = "Low";

    public bool ActiveWindowRelated { get; set; }

    public int MinDataContextDepth { get; set; } = -1;

    public IReadOnlyList<string> OwnerPaths { get; set; } = [];

    public IReadOnlyList<string> RiskFlags { get; set; } = [];
}
