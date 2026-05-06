namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmTimelineReflectionResult
{
    public RuntimeEnvironmentKind RuntimeKind { get; set; } = RuntimeEnvironmentKind.Unknown;

    public string ProcessName { get; set; } = string.Empty;

    public bool TimelineViewFound { get; set; }

    public bool TimelineViewModelFound { get; set; }

    public bool UndoRedoManagerFound { get; set; }

    public bool AsyncAwaitStatusFound { get; set; }

    public bool SetTimelineToolInfoFound { get; set; }

    public string? TimelineViewTypeName { get; set; }

    public string? TimelineViewModelTypeName { get; set; }

    public string? SetTimelineToolInfoOwnerTypeName { get; set; }

    public IReadOnlyList<string> ConstructorSignatures { get; set; } = [];

    public IReadOnlyList<string> MissingDependencies { get; set; } = [];

    public IReadOnlyList<string> Notes { get; set; } = [];

    public IReadOnlyList<string> FoundAssemblies { get; set; } = [];
    public IReadOnlyList<string> YmmRelatedAssemblyNames { get; set; } = [];
    public IReadOnlyList<string> CandidateAssemblyNames { get; set; } = [];

    public int AssemblyCount { get; set; }

    public int TypeFoundCount { get; set; }

    public long ProbeMs { get; set; }

    public bool CanAttemptExperimentalHost { get; set; }

    public IReadOnlyList<YmmRuntimeDependencyCandidate> SceneCandidates { get; set; } = [];

    public IReadOnlyList<YmmRuntimeDependencyCandidate> UndoRedoManagerCandidates { get; set; } = [];

    public IReadOnlyList<YmmRuntimeDependencyCandidate> AsyncAwaitStatusCandidates { get; set; } = [];
}
