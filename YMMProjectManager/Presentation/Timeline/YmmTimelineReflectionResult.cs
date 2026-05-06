namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmTimelineReflectionResult
{
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

    public int AssemblyCount { get; set; }

    public int TypeFoundCount { get; set; }

    public long ProbeMs { get; set; }

    public bool CanAttemptExperimentalHost { get; set; }
}
