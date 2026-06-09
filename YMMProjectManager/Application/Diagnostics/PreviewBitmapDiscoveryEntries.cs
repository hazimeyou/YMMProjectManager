namespace YMMProjectManager.Application.Diagnostics;

public sealed class PreviewBitmapWindowInfo
{
    public string? Type { get; init; }

    public string? Name { get; init; }

    public string? Title { get; init; }

    public string? DataContextType { get; init; }

    public bool IsActive { get; init; }

    public string? Visibility { get; init; }
}

public sealed class PreviewBitmapVisualTreeInfo
{
    public string? WindowType { get; init; }

    public string? ControlType { get; init; }

    public string? ControlName { get; init; }

    public string? DataContextType { get; init; }

    public int Depth { get; init; }

    public string? ParentType { get; init; }
}

public sealed class PreviewBitmapCandidateInfo
{
    public string? FoundType { get; init; }

    public string? Assembly { get; init; }

    public string? Parent { get; init; }

    public string? DataContext { get; init; }

    public string? SourceKind { get; init; }

    public string? ControlType { get; init; }

    public string? ControlName { get; init; }

    public string? DataContextType { get; init; }

    public int? Depth { get; init; }
}

public sealed class PreviewBitmapMethodGroupInfo
{
    public string? Type { get; init; }

    public string? Assembly { get; init; }

    public string? DataContextType { get; init; }

    public IReadOnlyList<PreviewBitmapMethodInfo> Methods { get; init; } = [];
}

public sealed class PreviewBitmapMethodInfo
{
    public string? Name { get; init; }

    public string? ReturnType { get; init; }

    public int ParameterCount { get; init; }

    public IReadOnlyList<string> ParameterTypes { get; init; } = [];

    public bool IsPublic { get; init; }

    public bool IsPrivate { get; init; }

    public bool IsFamily { get; init; }

    public bool IsAssembly { get; init; }

    public bool IsStatic { get; init; }

    public string? MatchKeyword { get; init; }
}

public sealed class PreviewBitmapMethodSignatureParameterInfo
{
    public string? Name { get; init; }

    public string? Type { get; init; }

    public bool HasDefaultValue { get; init; }

    public string? DefaultValue { get; init; }
}

public sealed class PreviewBitmapMethodSignatureInfo
{
    public string? MethodName { get; init; }

    public string? DeclaringType { get; init; }

    public string? ReturnType { get; init; }

    public int ParameterCount { get; init; }

    public IReadOnlyList<PreviewBitmapMethodSignatureParameterInfo> Parameters { get; init; } = [];

    public bool IsPublic { get; init; }

    public bool IsStatic { get; init; }

    public string? MatchKeyword { get; init; }

    public string? Category { get; init; }

    public IReadOnlyList<string> InvocationCandidates { get; init; } = [];
}

public sealed class PreviewBitmapCaptureResult
{
    public bool InvocationSucceeded { get; init; }

    public string? ReturnType { get; init; }

    public string? BitmapType { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public string? PixelFormat { get; init; }

    public bool SaveSucceeded { get; init; }

    public long? FileSize { get; init; }

    public string? SavedFilePath { get; init; }

    public string? ExceptionType { get; init; }

    public string? ExceptionMessage { get; init; }

    public double DurationMs { get; init; }

    public bool CaptureSucceeded { get; init; }

    public bool HasAlpha { get; init; }

    public string? FailureKind { get; init; }
}

public sealed class PreviewBitmapComparisonResult
{
    public bool FalseSucceeded { get; init; }

    public bool TrueSucceeded { get; init; }

    public bool FalseInvocationSucceeded { get; init; }

    public bool TrueInvocationSucceeded { get; init; }

    public bool FalseCaptureSucceeded { get; init; }

    public bool TrueCaptureSucceeded { get; init; }

    public int? FalseWidth { get; init; }

    public int? FalseHeight { get; init; }

    public string? FalsePixelFormat { get; init; }

    public bool FalseHasAlpha { get; init; }

    public long? FalseFileSize { get; init; }

    public double FalseDurationMs { get; init; }

    public string? FalseFailureKind { get; init; }

    public int? TrueWidth { get; init; }

    public int? TrueHeight { get; init; }

    public string? TruePixelFormat { get; init; }

    public bool TrueHasAlpha { get; init; }

    public long? TrueFileSize { get; init; }

    public double TrueDurationMs { get; init; }

    public string? TrueFailureKind { get; init; }

    public string? PreferredCall { get; init; }

    public string? Reason { get; init; }
}

public sealed class PreviewBitmapHistoryEntry
{
    public DateTimeOffset Timestamp { get; init; }

    public bool FalseCaptureSucceeded { get; init; }

    public bool TrueCaptureSucceeded { get; init; }

    public bool FalseInvocationSucceeded { get; init; }

    public bool TrueInvocationSucceeded { get; init; }

    public bool FalseBitmapSaveSucceeded { get; init; }

    public bool TrueBitmapSaveSucceeded { get; init; }

    public string? PreferredCall { get; init; }

    public string? Reason { get; init; }
}
