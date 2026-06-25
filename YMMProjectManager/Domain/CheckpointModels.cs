using System;
using System.Collections.Generic;
using YukkuriMovieMaker.Plugin;

namespace YMMProjectManager.Domain;

public enum CheckpointThumbnailMode
{
    EvenSplit,
    Every1Second,
    Every5Seconds,
    Every10Seconds,
    Every30Seconds,
    Every1Minute,
    Every5Minutes,
    CustomSeconds,
    CustomMinutes,
}

public sealed class CheckpointThumbnailSettings
{
    public CheckpointThumbnailMode Mode { get; set; } = CheckpointThumbnailMode.EvenSplit;
    public int SampleCount { get; set; } = 64;
    public int CustomValue { get; set; }
    public bool IncludeLastFrame { get; set; } = true;
}

public sealed class CheckpointManifest
{
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectPath { get; set; } = string.Empty;
    public string ProjectFileName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<CheckpointManifestItem> Checkpoints { get; set; } = [];
}

public sealed class CheckpointManifestItem
{
    public string CheckpointId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string YmmpPath { get; set; } = "project.ymmp";
    public string YmmpxPath { get; set; } = "project.ymmpx";
    public string? RepresentativeThumbnailPath { get; set; }
    public List<string> ThumbnailPaths { get; set; } = [];
    public string ThumbnailMode { get; set; } = string.Empty;
    public int ThumbnailCustomValue { get; set; }
    public string? GitCommit { get; set; }
    public string? GitBranch { get; set; }
    public long YmmpFileSize { get; set; }
    public long YmmpxFileSize { get; set; }
    public string YmmpSha256 { get; set; } = string.Empty;
    public string? Comment { get; set; }
}

public sealed class CheckpointRecord
{
    public string CheckpointId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string ProjectPath { get; set; } = string.Empty;
    public string CheckpointDirectory { get; set; } = string.Empty;
    public string YmmpPath { get; set; } = string.Empty;
    public string YmmpxPath { get; set; } = string.Empty;
    public string? RepresentativeThumbnailPath { get; set; }
    public IReadOnlyList<string> ThumbnailPaths { get; set; } = Array.Empty<string>();
    public string ThumbnailMode { get; set; } = string.Empty;
    public int ThumbnailCustomValue { get; set; }
    public string? GitCommit { get; set; }
    public string? GitBranch { get; set; }
    public string YmmpSha256 { get; set; } = string.Empty;
    public long YmmpFileSize { get; set; }
    public long YmmpxFileSize { get; set; }
    public string? Comment { get; set; }
    public bool IsValid { get; set; }
    public string? IssueMessage { get; set; }
}

public sealed class CheckpointCreateRequest
{
    public string ProjectPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Comment { get; set; }
    public CheckpointThumbnailSettings ThumbnailSettings { get; set; } = new();
    public TimelineToolInfo? TimelineInfo { get; set; }
}

public sealed class CheckpointRestoreRequest
{
    public string ProjectPath { get; set; } = string.Empty;
    public string CheckpointId { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
}

public sealed class CheckpointRestoreResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? RestoredProjectPath { get; set; }
    public string? RestoredYmmpxPath { get; set; }
}

public sealed class CheckpointThumbnailPlan
{
    public required int[] Frames { get; init; }
    public required string ModeLabel { get; init; }
    public required int CustomValue { get; init; }
}

public sealed class CheckpointThumbnailGenerationResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? RepresentativeThumbnailPath { get; set; }
    public IReadOnlyList<string> ThumbnailPaths { get; set; } = Array.Empty<string>();
    public string ModeLabel { get; set; } = string.Empty;
    public int CustomValue { get; set; }
}
