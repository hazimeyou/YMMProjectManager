using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Infrastructure.Checkpoint;

public sealed class CheckpointMetadataService
{
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
}
