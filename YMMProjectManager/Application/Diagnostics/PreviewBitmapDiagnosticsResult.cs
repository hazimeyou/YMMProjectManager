namespace YMMProjectManager.Application.Diagnostics;

public sealed class PreviewBitmapDiagnosticsResult
{
    public bool PreviewViewModelFound { get; init; }

    public bool GetBitmapMethodFound { get; init; }

    public bool CaptureSucceeded { get; init; }

    public string? PreviewViewModelTypeName { get; init; }

    public string? GetBitmapReturnTypeName { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public string? SavedFilePath { get; init; }

    public string? FailureReason { get; init; }

    public TimeSpan Duration { get; init; }
}
