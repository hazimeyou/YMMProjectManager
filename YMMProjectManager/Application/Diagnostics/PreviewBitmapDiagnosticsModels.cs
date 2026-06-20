namespace YMMProjectManager.Application.Diagnostics;

/// <summary>
/// プレビュー画面の探索、GetBitmap 呼び出し、画像保存までの診断結果です。
/// </summary>
public sealed class PreviewBitmapDiagnosticsResult
{
    public bool DiscoverySucceeded { get; set; }
    public int DiscoveryLevelReached { get; set; }
    public int WindowCount { get; set; }
    public int VisualTreeElementCount { get; set; }
    public int PreviewCandidateCount { get; set; }
    public int PreviewMethodCount { get; set; }
    public int MethodSignatureCount { get; set; }
    public bool PreviewViewFound { get; set; }
    public bool PreviewViewModelFound { get; set; }
    public bool ScenePreviewViewModelFound { get; set; }
    public bool GetBitmapMethodFound { get; set; }
    public string? GetBitmapSignatureCategory { get; set; }
    public int? GetBitmapParameterCount { get; set; }
    public string[] GetBitmapParameterTypes { get; set; } = [];
    public List<string> GetBitmapInvocationCandidates { get; set; } = [];
    public string NextRecommendedCall { get; set; } = string.Empty;
    public bool FalseInvocationSucceeded { get; set; }
    public bool TrueInvocationSucceeded { get; set; }
    public bool FalseCaptureSucceeded { get; set; }
    public bool TrueCaptureSucceeded { get; set; }
    public bool FalseBitmapSaveSucceeded { get; set; }
    public bool TrueBitmapSaveSucceeded { get; set; }
    public string? FalseFailureKind { get; set; }
    public string? TrueFailureKind { get; set; }
    public bool? FalseHasAlpha { get; set; }
    public bool? TrueHasAlpha { get; set; }
    public int? FalseWidth { get; set; }
    public int? FalseHeight { get; set; }
    public string? FalsePixelFormat { get; set; }
    public long? FalseFileSize { get; set; }
    public double? FalseDurationMs { get; set; }
    public string? FalseSavedFilePath { get; set; }
    public int? TrueWidth { get; set; }
    public int? TrueHeight { get; set; }
    public string? TruePixelFormat { get; set; }
    public long? TrueFileSize { get; set; }
    public double? TrueDurationMs { get; set; }
    public string? TrueSavedFilePath { get; set; }
    public bool GetBitmapInvocationSucceeded { get; set; }
    public bool CaptureSucceeded { get; set; }
    public string? PreviewViewModelTypeName { get; set; }
    public string? GetBitmapReturnTypeName { get; set; }
    public int? BitmapWidth { get; set; }
    public int? BitmapHeight { get; set; }
    public string? BitmapPixelFormat { get; set; }
    public bool BitmapSaveSucceeded { get; set; }
    public string? SavedFilePath { get; set; }
    public string? FailureReason { get; set; }
    public string? DiagnosticWindowsPath { get; set; }
    public string? DiagnosticVisualTreePath { get; set; }
    public string? PreviewCandidatesPath { get; set; }
    public string? PreviewMethodsPath { get; set; }
    public string? MethodSignaturesPath { get; set; }
    public string? CaptureResultPath { get; set; }
    public string? FalseCaptureResultPath { get; set; }
    public string? TrueCaptureResultPath { get; set; }
    public string? ComparisonPath { get; set; }
    public string? HistoryPath { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// PreviewViewModel から見つかった GetBitmap 系メソッドの署名情報です。
/// </summary>
public sealed class PreviewBitmapMethodSignatureInfo
{
    public string MethodName { get; set; } = string.Empty;
    public string? DeclaringType { get; set; }
    public string? ReturnType { get; set; }
    public int ParameterCount { get; set; }
    public List<PreviewBitmapMethodSignatureParameterInfo> Parameters { get; set; } = [];
    public bool IsPublic { get; set; }
    public bool IsStatic { get; set; }
    public string? MatchKeyword { get; set; }
    public string? Category { get; set; }
    public List<string> InvocationCandidates { get; set; } = [];
}

/// <summary>
/// GetBitmap メソッドの個々の引数情報です。
/// </summary>
public sealed class PreviewBitmapMethodSignatureParameterInfo
{
    public string? Name { get; set; }
    public string? Type { get; set; }
    public bool HasDefaultValue { get; set; }
    public object? DefaultValue { get; set; }
}

/// <summary>
/// GetBitmap を 1 回呼び出した結果と、保存できた画像のメタデータです。
/// </summary>
public sealed class PreviewBitmapCaptureResult
{
    public bool InvocationSucceeded { get; set; }
    public string? ReturnType { get; set; }
    public string? BitmapType { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? PixelFormat { get; set; }
    public bool SaveSucceeded { get; set; }
    public long? FileSize { get; set; }
    public string? SavedFilePath { get; set; }
    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }
    public double DurationMs { get; set; }
    public bool CaptureSucceeded { get; set; }
    public bool HasAlpha { get; set; }
    public string? FailureKind { get; set; }
}

/// <summary>
/// GetBitmap(false) と GetBitmap(true) の試行結果を比較するための情報です。
/// </summary>
public sealed class PreviewBitmapComparisonResult
{
    public bool FalseSucceeded { get; set; }
    public bool TrueSucceeded { get; set; }
    public bool FalseInvocationSucceeded { get; set; }
    public bool TrueInvocationSucceeded { get; set; }
    public bool FalseCaptureSucceeded { get; set; }
    public bool TrueCaptureSucceeded { get; set; }
    public int? FalseWidth { get; set; }
    public int? FalseHeight { get; set; }
    public string? FalsePixelFormat { get; set; }
    public bool? FalseHasAlpha { get; set; }
    public long? FalseFileSize { get; set; }
    public double? FalseDurationMs { get; set; }
    public string? FalseFailureKind { get; set; }
    public int? TrueWidth { get; set; }
    public int? TrueHeight { get; set; }
    public string? TruePixelFormat { get; set; }
    public bool? TrueHasAlpha { get; set; }
    public long? TrueFileSize { get; set; }
    public double? TrueDurationMs { get; set; }
    public string? TrueFailureKind { get; set; }
    public string? PreferredCall { get; set; }
    public string? Reason { get; set; }
}
