using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Infrastructure.Checkpoint;

public sealed class CheckpointMetadataService
{
    public bool TryLoad(CheckpointManifestItem? item, out CheckpointManifestItem metadata, out string errorMessage)
    {
        if (item is null)
        {
            metadata = new CheckpointManifestItem();
            errorMessage = "metadata.json を読み込めませんでした。";
            return false;
        }

        metadata = item;
        errorMessage = string.Empty;
        return true;
    }

    public CheckpointManifest CreateEmptyManifest(string projectId, string projectPath, DateTimeOffset createdAt)
    {
        return new CheckpointManifest
        {
            ProjectId = projectId,
            ProjectPath = projectPath,
            ProjectFileName = Path.GetFileName(projectPath),
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
    }

    public async Task<CheckpointManifestItem> BuildAsync(
        string checkpointId,
        string projectPath,
        string checkpointDirectory,
        string name,
        string? description,
        string? comment,
        DateTimeOffset createdAt,
        CheckpointThumbnailGenerationResult thumbnails,
        string? gitCommit,
        string? gitBranch,
        CancellationToken cancellationToken = default)
    {
        var ymmpPath = Path.Combine(checkpointDirectory, "project.ymmp");
        var ymmpxPath = Path.Combine(checkpointDirectory, "project.ymmpx");

        var sha256 = await Task.Run(() =>
        {
            using var stream = File.OpenRead(ymmpPath);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }, cancellationToken).ConfigureAwait(false);

        return new CheckpointManifestItem
        {
            CheckpointId = checkpointId,
            Name = string.IsNullOrWhiteSpace(name) ? createdAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) : name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            CreatedAt = createdAt,
            YmmpPath = "project.ymmp",
            YmmpxPath = "project.ymmpx",
            RepresentativeThumbnailPath = MakeRelativePath(checkpointDirectory, thumbnails.RepresentativeThumbnailPath),
            ThumbnailPaths = thumbnails.ThumbnailPaths.Select(path => MakeRelativePath(checkpointDirectory, path) ?? string.Empty).Where(path => !string.IsNullOrWhiteSpace(path)).ToList(),
            ThumbnailMode = thumbnails.ModeLabel,
            ThumbnailCustomValue = thumbnails.CustomValue,
            GitCommit = gitCommit,
            GitBranch = gitBranch,
            YmmpFileSize = new FileInfo(ymmpPath).Length,
            YmmpxFileSize = File.Exists(ymmpxPath) ? new FileInfo(ymmpxPath).Length : 0,
            YmmpSha256 = sha256,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
        };
    }

    private static string? MakeRelativePath(string baseDirectory, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Path.GetRelativePath(baseDirectory, path);
    }

    public List<CheckpointDiagnosticItem> Validate(CheckpointRecord? record, bool manifestExists, bool manifestLoaded)
    {
        var items = new List<CheckpointDiagnosticItem>();
        items.Add(new CheckpointDiagnosticItem
        {
            Severity = manifestExists ? CheckpointDiagnosticSeverity.Ok : CheckpointDiagnosticSeverity.Error,
            Title = "manifest",
            Message = manifestExists ? "manifest が存在します" : "manifest が存在しません",
        });
        items.Add(new CheckpointDiagnosticItem
        {
            Severity = manifestLoaded ? CheckpointDiagnosticSeverity.Ok : CheckpointDiagnosticSeverity.Error,
            Title = "manifest-read",
            Message = manifestLoaded ? "manifest を読み込めます" : "manifest を読み込めません",
        });

        if (record is null)
        {
            items.Add(new CheckpointDiagnosticItem
            {
                Severity = CheckpointDiagnosticSeverity.Error,
                Title = "metadata",
                Message = "チェックポイントの metadata を取得できません",
            });
            return items;
        }

        items.Add(CreateExistsItem("ymmp", record.YmmpPath, true));
        items.Add(CreateExistsItem("ymmpx", record.YmmpxPath, true));
        items.Add(CreateExistsItem("representative-thumbnail", record.RepresentativeThumbnailPath, false, "代表サムネイル"));

        if (record.ThumbnailPaths.Count == 0)
        {
            items.Add(new CheckpointDiagnosticItem
            {
                Severity = CheckpointDiagnosticSeverity.Warning,
                Title = "thumbnails",
                Message = "サムネイル一覧が記録されていません",
            });
        }
        else
        {
            var missingCount = record.ThumbnailPaths.Count(path => !File.Exists(path));
            items.Add(new CheckpointDiagnosticItem
            {
                Severity = missingCount == 0 ? CheckpointDiagnosticSeverity.Ok : CheckpointDiagnosticSeverity.Error,
                Title = "thumbnails",
                Message = missingCount == 0
                    ? $"サムネイル一覧が存在します ({record.ThumbnailPaths.Count}件)"
                    : $"サムネイル一覧に不足があります ({missingCount}件不足)",
            });
        }

        items.Add(new CheckpointDiagnosticItem
        {
            Severity = record.IsValid ? CheckpointDiagnosticSeverity.Ok : CheckpointDiagnosticSeverity.Error,
            Title = "restore",
            Message = record.IsValid ? "復元に必要な主要ファイルが存在します" : $"復元に必要なファイルが不足しています: {record.IssueMessage ?? "不明"}",
        });
        items.Add(new CheckpointDiagnosticItem
        {
            Severity = string.IsNullOrWhiteSpace(record.GitCommit) ? CheckpointDiagnosticSeverity.Warning : CheckpointDiagnosticSeverity.Ok,
            Title = "git-commit",
            Message = string.IsNullOrWhiteSpace(record.GitCommit) ? "Git Commit が記録されていません" : $"Git Commit が記録されています: {record.GitCommit}",
        });
        items.Add(new CheckpointDiagnosticItem
        {
            Severity = string.IsNullOrWhiteSpace(record.GitBranch) ? CheckpointDiagnosticSeverity.Warning : CheckpointDiagnosticSeverity.Ok,
            Title = "git-branch",
            Message = string.IsNullOrWhiteSpace(record.GitBranch) ? "Git Branch が記録されていません" : $"Git Branch が記録されています: {record.GitBranch}",
        });

        return items;
    }

    private static CheckpointDiagnosticItem CreateExistsItem(string title, string? path, bool required, string? displayName = null)
    {
        var name = displayName ?? title;
        if (string.IsNullOrWhiteSpace(path))
        {
            return new CheckpointDiagnosticItem
            {
                Severity = required ? CheckpointDiagnosticSeverity.Error : CheckpointDiagnosticSeverity.Warning,
                Title = title,
                Message = $"{name} のパスが記録されていません",
            };
        }

        var exists = File.Exists(path);
        return new CheckpointDiagnosticItem
        {
            Severity = exists ? CheckpointDiagnosticSeverity.Ok : (required ? CheckpointDiagnosticSeverity.Error : CheckpointDiagnosticSeverity.Warning),
            Title = title,
            Message = exists ? $"{name} が存在します" : $"{name} が存在しません",
        };
    }
}
