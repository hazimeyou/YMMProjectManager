using System.Reflection;
using System.Text.Json.Serialization;
using YMMProjectManager.Application.Diagnostics;

namespace YMMProjectManager.Application.Thumbnails;

public sealed class YmmPreviewDiscoveryOptions
{
    public int DiscoveryRetryCount { get; init; } = 5;

    public int DiscoveryRetryDelayMs { get; init; } = 100;

    public int MaxCandidateLogCount { get; init; } = 100;
}

public sealed class YmmPreviewDiscoveryResult
{
    public bool DiscoverySucceeded { get; init; }

    public string? FailureReason { get; init; }

    public string? FailureStage { get; init; }

    public int DiscoveryLevelReached { get; init; }

    public int WindowCount { get; init; }

    public int VisualTreeElementCount { get; init; }

    public int PreviewCandidateCount { get; init; }

    public int PreviewMethodCount { get; init; }

    public int MethodSignatureCount { get; init; }

    public bool PreviewViewFound { get; init; }

    public string? PreviewViewDataContextType { get; init; }

    public bool PreviewViewModelFound { get; init; }

    public bool ScenePreviewViewModelFound { get; init; }

    public bool GetBitmapMethodFound { get; init; }

    public string? PreviewViewModelTypeName { get; init; }

    public string? GetBitmapReturnTypeName { get; init; }

    public string? GetBitmapSignatureCategory { get; init; }

    public int? GetBitmapParameterCount { get; init; }

    public IReadOnlyList<string> GetBitmapParameterTypes { get; init; } = [];

    public IReadOnlyList<string> GetBitmapInvocationCandidates { get; init; } = [];

    public string? NextRecommendedCall { get; init; }

    public IReadOnlyList<PreviewBitmapWindowInfo> Windows { get; init; } = [];

    public IReadOnlyList<PreviewBitmapVisualTreeInfo> VisualTree { get; init; } = [];

    public IReadOnlyList<PreviewBitmapCandidateInfo> Candidates { get; init; } = [];

    public IReadOnlyList<PreviewBitmapMethodGroupInfo> Methods { get; init; } = [];

    public IReadOnlyList<PreviewBitmapMethodSignatureInfo> MethodSignatures { get; init; } = [];

    public IReadOnlyList<string> CandidateDataContextTypes { get; init; } = [];

    public IReadOnlyList<string> CandidatePropertyNames { get; init; } = [];

    public IReadOnlyList<string> CandidateMethodNames { get; init; } = [];

    [JsonIgnore]
    public object? TargetInstance { get; init; }

    [JsonIgnore]
    public Type? TargetType { get; init; }

    [JsonIgnore]
    public MethodInfo? TargetMethod { get; init; }

    [JsonIgnore]
    public object?[] TargetArguments { get; init; } = [];

    public string? DiscoveryFailureReason { get; init; }

    public string? SignatureFailureReason { get; init; }

    public string? InvocationFailureReason { get; init; }

    public string? BitmapSaveFailureReason { get; init; }

    public string? OverallFailureReason { get; init; }
}

public sealed class YmmPreviewDiscoveryFailureReport
{
    public DateTimeOffset Timestamp { get; init; }

    public string? FailureReason { get; init; }

    public string? FailureStage { get; init; }

    public int WindowCount { get; init; }

    public int VisualTreeElementCount { get; init; }

    public bool PreviewViewFound { get; init; }

    public bool PreviewViewModelFound { get; init; }

    public bool GetBitmapMethodFound { get; init; }

    public int DiscoveryLevelReached { get; init; }

    public IReadOnlyList<string> CandidateTypes { get; init; } = [];

    public IReadOnlyList<string> CandidateMethods { get; init; } = [];

    public IReadOnlyList<string> CandidateDataContexts { get; init; } = [];
}
