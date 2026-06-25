namespace YMMProjectManager.Domain.Packaging;

public sealed class YmmpxExtractResult
{
    public bool Success { get; init; }

    public string? ErrorMessage { get; init; }

    public string? RestoredProjectPath { get; init; }

    public string? OutputDirectory { get; init; }

    public int ReplacedPathCount { get; init; }

    public int ExtractedResourceCount { get; init; }

    public IReadOnlyList<YmmpxPackageLink> Links { get; init; } = [];
}
