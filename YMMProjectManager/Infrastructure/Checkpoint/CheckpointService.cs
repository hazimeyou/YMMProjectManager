using System.Diagnostics;
using System.IO;
using YMMProjectManager.Application;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure.Output;
using YMMProjectManager.Infrastructure.Packaging;

namespace YMMProjectManager.Infrastructure.Checkpoint;

public sealed class CheckpointService : ICheckpointService
{
    private readonly CheckpointStorage storage;
    private readonly CheckpointMetadataService metadataService;
    private readonly CheckpointExporter exporter;
    private readonly CheckpointRestorer restorer;
    private readonly ThumbnailGenerationService thumbnailGenerationService;
    private readonly CheckpointLogger checkpointLogger;

    public CheckpointService(FileLogger logger, string? storageRootDirectory = null)
    {
        storage = new CheckpointStorage(storageRootDirectory);
        metadataService = new CheckpointMetadataService();
        var bundleService = new YmmpxLibBundleService(logger);
        exporter = new CheckpointExporter(storage, bundleService);
        restorer = new CheckpointRestorer(storage, bundleService);
        thumbnailGenerationService = new ThumbnailGenerationService(logger, new CurrentPreviewCaptureService(logger), new ThumbnailIntervalPlanner());
        checkpointLogger = new CheckpointLogger(logger);
    }

    public string GetProjectDirectory(string projectPath) => storage.GetProjectDirectory(projectPath);

    public async Task<CheckpointRecord> CreateAsync(CheckpointCreateRequest request, CancellationToken cancellationToken = default)
    {
        var projectPath = Path.GetFullPath(request.ProjectPath);
        var createdAt = DateTimeOffset.Now;
        var checkpointId = storage.CreateCheckpointId(createdAt);
        var checkpointDirectory = storage.GetCheckpointDirectory(projectPath, checkpointId);

        storage.EnsureProjectLayout(projectPath);
        checkpointLogger.CreationStarted(projectPath, checkpointId, request.Name);

        try
        {
            Directory.CreateDirectory(checkpointDirectory);
            await exporter.ExportAsync(projectPath, checkpointId, cancellationToken).ConfigureAwait(false);

            if (request.TimelineInfo is null)
            {
                throw new InvalidOperationException("サムネイル生成には、対象プロジェクトを YMM で開いている必要があります。");
            }

            var thumbnails = await thumbnailGenerationService
                .GenerateAsync(storage.GetThumbnailsDirectory(projectPath, checkpointId), request.ThumbnailSettings, request.TimelineInfo, cancellationToken)
                .ConfigureAwait(true);
            if (!thumbnails.Success)
            {
                throw new InvalidOperationException(thumbnails.ErrorMessage ?? "サムネイル生成に失敗しました。");
            }

            var gitInfo = await GetGitInfoAsync(projectPath, cancellationToken).ConfigureAwait(false);
            var metadata = await metadataService
                .BuildAsync(checkpointId, projectPath, checkpointDirectory, request.Name, request.Description, request.Comment, createdAt, thumbnails, gitInfo.Commit, gitInfo.Branch, cancellationToken)
                .ConfigureAwait(false);

            await storage.WriteMetadataAsync(projectPath, checkpointId, metadata, cancellationToken).ConfigureAwait(false);

            var manifest = await storage.ReadManifestAsync(projectPath, cancellationToken).ConfigureAwait(false)
                ?? metadataService.CreateEmptyManifest(storage.GetProjectId(projectPath), projectPath, createdAt);
            manifest.ProjectId = storage.GetProjectId(projectPath);
            manifest.ProjectPath = projectPath;
            manifest.ProjectFileName = Path.GetFileName(projectPath);
            manifest.UpdatedAt = createdAt;
            manifest.Checkpoints.RemoveAll(x => string.Equals(x.CheckpointId, checkpointId, StringComparison.OrdinalIgnoreCase));
            manifest.Checkpoints.Add(metadata);
            await storage.WriteManifestAsync(projectPath, manifest, cancellationToken).ConfigureAwait(false);

            checkpointLogger.CreationSucceeded(projectPath, checkpointId);
            return ToRecord(projectPath, metadata);
        }
        catch (Exception ex)
        {
            checkpointLogger.CreationFailed(ex, projectPath, checkpointId);
            if (Directory.Exists(checkpointDirectory))
            {
                Directory.Delete(checkpointDirectory, recursive: true);
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<CheckpointRecord>> GetCheckpointsAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var normalizedPath = Path.GetFullPath(projectPath);
        var manifest = await storage.ReadManifestAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            return Array.Empty<CheckpointRecord>();
        }

        var results = new List<CheckpointRecord>();
        foreach (var item in manifest.Checkpoints.OrderByDescending(x => x.CreatedAt))
        {
            var metadata = await storage.ReadMetadataAsync(normalizedPath, item.CheckpointId, cancellationToken).ConfigureAwait(false) ?? item;
            var record = ToRecord(normalizedPath, metadata);
            record.IsValid = File.Exists(record.YmmpPath) && File.Exists(record.YmmpxPath);
            record.IssueMessage = record.IsValid ? null : "ymmp または ymmpx が見つかりません。";
            results.Add(record);
        }

        return results;
    }

    public async Task<CheckpointRecord?> GetCheckpointAsync(string projectPath, string checkpointId, CancellationToken cancellationToken = default)
    {
        var normalizedPath = Path.GetFullPath(projectPath);
        var metadata = await storage.ReadMetadataAsync(normalizedPath, checkpointId, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return null;
        }

        var record = ToRecord(normalizedPath, metadata);
        record.IsValid = File.Exists(record.YmmpPath) && File.Exists(record.YmmpxPath);
        record.IssueMessage = record.IsValid ? null : "ymmp または ymmpx が見つかりません。";
        return record;
    }

    public async Task<CheckpointRestoreResult> RestoreAsync(CheckpointRestoreRequest request, CancellationToken cancellationToken = default)
    {
        var projectPath = Path.GetFullPath(request.ProjectPath);
        checkpointLogger.RestoreStarted(projectPath, request.CheckpointId, request.OutputDirectory);
        try
        {
            var result = await restorer.RestoreAsync(projectPath, request.CheckpointId, request.OutputDirectory, cancellationToken).ConfigureAwait(false);
            if (result.Success && !string.IsNullOrWhiteSpace(result.RestoredProjectPath))
            {
                checkpointLogger.RestoreSucceeded(projectPath, request.CheckpointId, result.RestoredProjectPath);
            }

            return result;
        }
        catch (Exception ex)
        {
            checkpointLogger.RestoreFailed(ex, projectPath, request.CheckpointId);
            return new CheckpointRestoreResult
            {
                Success = false,
                ErrorMessage = ex.Message,
            };
        }
    }

    private CheckpointRecord ToRecord(string projectPath, CheckpointManifestItem item)
    {
        var checkpointDirectory = storage.GetCheckpointDirectory(projectPath, item.CheckpointId);
        return new CheckpointRecord
        {
            CheckpointId = item.CheckpointId,
            Name = item.Name,
            Description = item.Description,
            CreatedAt = item.CreatedAt,
            ProjectPath = projectPath,
            CheckpointDirectory = checkpointDirectory,
            YmmpPath = Path.Combine(checkpointDirectory, item.YmmpPath),
            YmmpxPath = Path.Combine(checkpointDirectory, item.YmmpxPath),
            RepresentativeThumbnailPath = ResolvePath(checkpointDirectory, item.RepresentativeThumbnailPath),
            ThumbnailPaths = item.ThumbnailPaths.Select(path => ResolvePath(checkpointDirectory, path) ?? string.Empty).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray(),
            ThumbnailMode = item.ThumbnailMode,
            ThumbnailCustomValue = item.ThumbnailCustomValue,
            GitCommit = item.GitCommit,
            GitBranch = item.GitBranch,
            YmmpSha256 = item.YmmpSha256,
            YmmpFileSize = item.YmmpFileSize,
            YmmpxFileSize = item.YmmpxFileSize,
            Comment = item.Comment,
        };
    }

    private static string? ResolvePath(string baseDirectory, string? relativePath)
        => string.IsNullOrWhiteSpace(relativePath) ? null : Path.GetFullPath(Path.Combine(baseDirectory, relativePath));

    private static async Task<(string? Commit, string? Branch)> GetGitInfoAsync(string projectPath, CancellationToken cancellationToken)
    {
        var workingDirectory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return (null, null);
        }

        return (await RunGitAsync("rev-parse HEAD", workingDirectory, cancellationToken).ConfigureAwait(false),
            await RunGitAsync("branch --show-current", workingDirectory, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<string?> RunGitAsync(string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
