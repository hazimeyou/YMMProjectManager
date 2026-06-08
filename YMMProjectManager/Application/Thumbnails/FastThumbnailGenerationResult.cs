using YMMProjectManager.Infrastructure.Thumbnails;

namespace YMMProjectManager.Application.Thumbnails;

public sealed class FastThumbnailGenerationResult
{
    public bool Success { get; init; }

    public int RequestedSampleCount { get; init; }

    public int CapturedCount { get; init; }

    public TimeSpan Duration { get; init; }

    public string? FallbackReason { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public ThumbnailGenerationDiagnostics? Diagnostics { get; init; }
}
