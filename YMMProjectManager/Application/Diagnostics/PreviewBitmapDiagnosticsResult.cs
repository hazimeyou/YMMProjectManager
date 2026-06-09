namespace YMMProjectManager.Application.Diagnostics;

public sealed class PreviewBitmapDiagnosticsResult
{
    public bool DiscoverySucceeded { get; init; }

    public int DiscoveryLevelReached { get; init; }

    public int WindowCount { get; init; }

    public int VisualTreeElementCount { get; init; }

    public int PreviewCandidateCount { get; init; }

    public int PreviewMethodCount { get; init; }

    public int MethodSignatureCount { get; init; }

    public bool PreviewViewFound { get; init; }

    public bool PreviewViewModelFound { get; init; }

    public bool ScenePreviewViewModelFound { get; init; }

    public bool GetBitmapMethodFound { get; init; }

    public string? GetBitmapSignatureCategory { get; init; }

    public int? GetBitmapParameterCount { get; init; }

    public IReadOnlyList<string> GetBitmapParameterTypes { get; init; } = [];

    public IReadOnlyList<string> GetBitmapInvocationCandidates { get; init; } = [];

    public string? NextRecommendedCall { get; init; }

    public bool FalseInvocationSucceeded { get; init; }

    public bool TrueInvocationSucceeded { get; init; }

    public bool FalseCaptureSucceeded { get; init; }

    public bool TrueCaptureSucceeded { get; init; }

    public bool FalseBitmapSaveSucceeded { get; init; }

    public bool TrueBitmapSaveSucceeded { get; init; }

    public string? FalseFailureKind { get; init; }

    public string? TrueFailureKind { get; init; }

    public bool? FalseHasAlpha { get; init; }

    public bool? TrueHasAlpha { get; init; }

    public int? FalseWidth { get; init; }

    public int? FalseHeight { get; init; }

    public string? FalsePixelFormat { get; init; }

    public long? FalseFileSize { get; init; }

    public double? FalseDurationMs { get; init; }

    public string? FalseSavedFilePath { get; init; }

    public int? TrueWidth { get; init; }

    public int? TrueHeight { get; init; }

    public string? TruePixelFormat { get; init; }

    public long? TrueFileSize { get; init; }

    public double? TrueDurationMs { get; init; }

    public string? TrueSavedFilePath { get; init; }

    public string? ComparisonPath { get; init; }

    public string? FalseCaptureResultPath { get; init; }

    public string? TrueCaptureResultPath { get; init; }

    public string? DiscoveryFailureReason { get; init; }

    public string? SignatureFailureReason { get; init; }

    public string? InvocationFailureReason { get; init; }

    public string? BitmapSaveFailureReason { get; init; }

    public string? CaptureFailureReason { get; init; }

    public string? CaptureFailureKind { get; init; }

    public string? OverallFailureReason { get; init; }

    public bool GetBitmapInvocationSucceeded { get; init; }

    public bool CaptureSucceeded { get; init; }

    public string? PreviewViewModelTypeName { get; init; }

    public string? GetBitmapReturnTypeName { get; init; }

    public int? BitmapWidth { get; init; }

    public int? BitmapHeight { get; init; }

    public string? BitmapPixelFormat { get; init; }

    public bool BitmapSaveSucceeded { get; init; }

    public string? SavedFilePath { get; init; }

    public string? FailureReason { get; init; }

    public string? DiagnosticWindowsPath { get; init; }

    public string? DiagnosticVisualTreePath { get; init; }

    public string? PreviewCandidatesPath { get; init; }

    public string? PreviewMethodsPath { get; init; }

    public string? MethodSignaturesPath { get; init; }

    public string? CaptureResultPath { get; init; }

    public string? HistoryPath { get; init; }

    public TimeSpan Duration { get; init; }
}
