namespace YMMProjectManager.Domain.Packaging;

public sealed class YmmpxPackageResult
{
    public bool Success { get; init; }

    public string? ErrorMessage { get; init; }

    public string? OutputPath { get; init; }

    public int DetectedMaterialCount { get; init; }

    public int PackagedMaterialCount { get; init; }

    public int MissingMaterialCount { get; init; }

    public int DuplicateMaterialCount { get; init; }

    public IReadOnlyList<YmmpxPackageLink> Links { get; init; } = [];
}

public sealed class YmmpxPackageLink
{
    public string OriginalPath { get; init; } = string.Empty;

    public string? ResolvedPath { get; init; }

    public string BundlePath { get; init; } = string.Empty;

    public bool Exists { get; init; }

    public string? Status { get; init; }
}
