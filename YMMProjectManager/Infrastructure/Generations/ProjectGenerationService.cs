using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using YMMProjectManager.Application;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Infrastructure.Generations;

public sealed class ProjectGenerationService : IProjectGenerationService
{
    private readonly FileLogger logger;
    private readonly ProjectGenerationHashService hashService;
    private readonly ProjectGenerationIdFactory idFactory;
    private readonly ProjectGenerationStorage storage;
    private readonly ProjectGenerationManifestRepository manifestRepository;

    public ProjectGenerationService(FileLogger logger, string? storageRootDirectory = null)
    {
        this.logger = logger;
        hashService = new ProjectGenerationHashService();
        idFactory = new ProjectGenerationIdFactory();
        storage = new ProjectGenerationStorage(storageRootDirectory);
        manifestRepository = new ProjectGenerationManifestRepository(storage, logger);
    }

    public string GetRootDirectory() => storage.RootDirectory;

    public string GetProjectDirectory(string projectPath) => storage.GetProjectDirectory(hashService.ComputeProjectId(projectPath));

    public async Task<ProjectGenerationRecord> CreateGenerationAsync(string projectPath, string displayName, string? memo, CancellationToken cancellationToken = default)
    {
        var normalizedProjectPath = NormalizeProjectPath(projectPath);
        var projectId = hashService.ComputeProjectId(normalizedProjectPath);
        storage.EnsureProjectLayout(projectId);

        var createdAt = DateTimeOffset.Now;
        var generationId = idFactory.Create(createdAt);
        var generationDirectory = storage.GetGenerationDirectory(projectId, generationId);
        var generationFilePath = storage.GetGenerationFilePath(projectId, generationId);
        var fileInfo = new FileInfo(normalizedProjectPath);
        var sha256 = await Task.Run(() => hashService.ComputeFileSha256(normalizedProjectPath), cancellationToken).ConfigureAwait(false);

        var manifestLoad = await manifestRepository.LoadAsync(projectId, normalizedProjectPath, cancellationToken).ConfigureAwait(false);
        var manifest = manifestLoad.Manifest;
        if (manifest.Generations.Count == 0)
        {
            manifest.CreatedAt = createdAt;
        }

        logger.Info($"GenerationCreateStarted projectPath={normalizedProjectPath}, generationId={generationId}");

        try
        {
            Directory.CreateDirectory(generationDirectory);
            await storage.CopyProjectSnapshotAsync(normalizedProjectPath, generationFilePath, cancellationToken).ConfigureAwait(false);

            var metadata = new ProjectGenerationManifestItem
            {
                GenerationId = generationId,
                CreatedAt = createdAt,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? FormatDefaultGenerationName(createdAt) : displayName.Trim(),
                Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim(),
                SourceProjectPath = normalizedProjectPath,
                FileSize = fileInfo.Length,
                Sha256 = sha256,
                CreatedByVersion = GetCreatedByVersion(),
            };

            await storage.WriteMetadataAsync(projectId, generationId, metadata, cancellationToken).ConfigureAwait(false);

            manifest.ProjectId = projectId;
            manifest.ProjectPath = normalizedProjectPath;
            manifest.ProjectFileName = Path.GetFileName(normalizedProjectPath);
            manifest.UpdatedAt = createdAt;
            manifest.Generations.RemoveAll(x => string.Equals(x.GenerationId, generationId, StringComparison.OrdinalIgnoreCase));
            manifest.Generations.Add(metadata);
            await manifestRepository.SaveAsync(projectId, manifest, cancellationToken).ConfigureAwait(false);

            logger.Info($"GenerationCreateCompleted projectPath={normalizedProjectPath}, generationId={generationId}, fileSize={metadata.FileSize}");
            return new ProjectGenerationRecord
            {
                GenerationId = metadata.GenerationId,
                CreatedAt = metadata.CreatedAt,
                DisplayName = metadata.DisplayName,
                Memo = metadata.Memo,
                SourceProjectPath = metadata.SourceProjectPath,
                FileSize = metadata.FileSize,
                Sha256 = metadata.Sha256,
                CreatedByVersion = metadata.CreatedByVersion,
                GenerationPath = generationDirectory,
                IsValid = true,
            };
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"GenerationCreateFailed projectPath={normalizedProjectPath}, generationId={generationId}");
            try
            {
                if (Directory.Exists(generationDirectory))
                {
                    Directory.Delete(generationDirectory, recursive: true);
                }
            }
            catch
            {
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<ProjectGenerationRecord>> GetGenerationsAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var normalizedProjectPath = NormalizeProjectPath(projectPath);
        var projectId = hashService.ComputeProjectId(normalizedProjectPath);
        var manifestLoad = await manifestRepository.LoadAsync(projectId, normalizedProjectPath, cancellationToken).ConfigureAwait(false);
        var manifest = manifestLoad.Manifest;
        var results = new List<ProjectGenerationRecord>();

        foreach (var item in manifest.Generations.OrderByDescending(x => x.CreatedAt))
        {
            try
            {
                var generationDirectory = storage.GetGenerationDirectory(projectId, item.GenerationId);
                var generationPath = storage.GetGenerationFilePath(projectId, item.GenerationId);
                var metadata = await storage.ReadMetadataAsync(projectId, item.GenerationId, cancellationToken).ConfigureAwait(false);
                var record = new ProjectGenerationRecord
                {
                    GenerationId = item.GenerationId,
                    CreatedAt = item.CreatedAt,
                    DisplayName = item.DisplayName,
                    Memo = item.Memo,
                    SourceProjectPath = item.SourceProjectPath,
                    FileSize = item.FileSize,
                    Sha256 = item.Sha256,
                    CreatedByVersion = item.CreatedByVersion,
                    GenerationPath = generationDirectory,
                };

                if (metadata is null)
                {
                    record.IsValid = false;
                    record.IssueMessage = "metadata.json が見つかりません。";
                    logger.Info($"GenerationMetadataCorrupted projectId={projectId}, generationId={item.GenerationId}, reason=metadata-missing");
                    results.Add(record);
                    continue;
                }

                record.DisplayName = string.IsNullOrWhiteSpace(metadata.DisplayName) ? record.DisplayName : metadata.DisplayName;
                record.Memo = metadata.Memo;
                record.SourceProjectPath = metadata.SourceProjectPath;
                record.FileSize = metadata.FileSize;
                record.Sha256 = metadata.Sha256;
                record.CreatedByVersion = metadata.CreatedByVersion;

                if (!File.Exists(generationPath))
                {
                    record.IsValid = false;
                    record.IssueMessage = "project.ymmp が見つかりません。";
                    logger.Info($"GenerationMetadataCorrupted projectId={projectId}, generationId={item.GenerationId}, reason=project-missing");
                }
                else
                {
                    var actualSha = hashService.ComputeFileSha256(generationPath);
                    if (!string.Equals(actualSha, metadata.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        record.IsValid = false;
                        record.IssueMessage = "SHA256 が一致しません。";
                        logger.Info($"GenerationMetadataCorrupted projectId={projectId}, generationId={item.GenerationId}, reason=sha-mismatch");
                    }
                    else
                    {
                        record.IsValid = true;
                    }
                }

                results.Add(record);
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"GenerationMetadataCorrupted projectId={projectId}, generationId={item.GenerationId}");
                results.Add(new ProjectGenerationRecord
                {
                    GenerationId = item.GenerationId,
                    CreatedAt = item.CreatedAt,
                    DisplayName = item.DisplayName,
                    Memo = item.Memo,
                    SourceProjectPath = item.SourceProjectPath,
                    FileSize = item.FileSize,
                    Sha256 = item.Sha256,
                    CreatedByVersion = item.CreatedByVersion,
                    GenerationPath = storage.GetGenerationDirectory(projectId, item.GenerationId),
                    IsValid = false,
                    IssueMessage = ex.Message,
                });
            }
        }

        return results;
    }

    public async Task<ProjectGenerationDiagnostics> GetDiagnosticsAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var normalizedProjectPath = NormalizeProjectPath(projectPath);
        var projectId = hashService.ComputeProjectId(normalizedProjectPath);
        var manifestLoad = await manifestRepository.LoadAsync(projectId, normalizedProjectPath, cancellationToken).ConfigureAwait(false);
        var generations = await GetGenerationsAsync(projectPath, cancellationToken).ConfigureAwait(false);
        var latest = generations.OrderByDescending(x => x.CreatedAt).FirstOrDefault(x => x.IsValid) ?? generations.OrderByDescending(x => x.CreatedAt).FirstOrDefault();

        var diagnostics = new ProjectGenerationDiagnostics
        {
            ProjectId = projectId,
            ProjectPath = normalizedProjectPath,
            GenerationCount = generations.Count(x => x.IsValid),
            StorageSize = storage.GetDirectorySizeBytes(storage.GetProjectDirectory(projectId)),
            LatestGeneration = latest?.GenerationId,
            LatestGenerationCreatedAt = latest?.CreatedAt,
            LatestGenerationDisplayName = latest?.DisplayName,
            ManifestStatus = manifestLoad.Status,
            DeletedGenerationCount = storage.CountChildDirectories(storage.GetDeletedDirectory(projectId)),
        };

        logger.Info(
            $"GenerationStorageScanned projectId={projectId}, projectPath={normalizedProjectPath}, generationCount={diagnostics.GenerationCount}, storageSize={diagnostics.StorageSize}, deletedCount={diagnostics.DeletedGenerationCount}, manifestStatus={diagnostics.ManifestStatus}");
        return diagnostics;
    }

    public async Task<(bool Success, string? ErrorMessage, ProjectGenerationRecord? Generation)> RestoreGenerationAsync(string projectPath, string generationId, GenerationRestoreMode restoreMode, CancellationToken cancellationToken = default)
    {
        var normalizedProjectPath = NormalizeProjectPath(projectPath);
        var projectId = hashService.ComputeProjectId(normalizedProjectPath);
        var generationPath = storage.GetGenerationFilePath(projectId, generationId);

        logger.Info($"GenerationRestoreStarted projectPath={normalizedProjectPath}, generationId={generationId}, mode={restoreMode}");

        string? tempPath = null;
        try
        {
            if (!File.Exists(generationPath))
            {
                return (false, "世代ファイルが見つかりません。", null);
            }

            var metadata = await storage.ReadMetadataAsync(projectId, generationId, cancellationToken).ConfigureAwait(false);
            if (metadata is null)
            {
                logger.Info($"GenerationMetadataCorrupted projectId={projectId}, generationId={generationId}, reason=metadata-missing");
                return (false, "metadata.json が見つかりません。", null);
            }

            var actualSha = hashService.ComputeFileSha256(generationPath);
            if (!string.Equals(actualSha, metadata.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.Info($"GenerationMetadataCorrupted projectId={projectId}, generationId={generationId}, reason=sha-mismatch");
                return (false, "SHA256 が一致しません。世代ファイルが破損している可能性があります。", null);
            }

            if (restoreMode == GenerationRestoreMode.RestoreAsNewFile)
            {
                var restoredPath = CreateRestoreAsNewFilePath(normalizedProjectPath, generationId);
                var tempDirectory = Path.GetDirectoryName(restoredPath) ?? AppContext.BaseDirectory;
                tempPath = await CreateValidatedRestoreTempAsync(generationPath, metadata.Sha256, tempDirectory, cancellationToken).ConfigureAwait(false);
                await storage.ReplaceFileAtomicallyAsync(tempPath, restoredPath, null, cancellationToken).ConfigureAwait(false);
                logger.Info($"GenerationRestoreCompleted projectPath={normalizedProjectPath}, generationId={generationId}, restoredPath={restoredPath}");
                return (true, null, BuildGenerationRecord(metadata, storage.GetGenerationDirectory(projectId, generationId)));
            }

            if (File.Exists(normalizedProjectPath))
            {
                TryOpenExclusive(normalizedProjectPath);
            }

            var backupPath = storage.ResolveRestoreBackupPath(projectId, normalizedProjectPath);
            tempPath = await CreateValidatedRestoreTempAsync(generationPath, metadata.Sha256, storage.GetGenerationDirectory(projectId, generationId), cancellationToken).ConfigureAwait(false);
            await storage.ReplaceFileAtomicallyAsync(tempPath, normalizedProjectPath, backupPath, cancellationToken).ConfigureAwait(false);

            logger.Info($"GenerationRestoreCompleted projectPath={normalizedProjectPath}, generationId={generationId}, backupPath={backupPath}");
            return (true, null, BuildGenerationRecord(metadata, storage.GetGenerationDirectory(projectId, generationId)));
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"GenerationRestoreFailed projectPath={normalizedProjectPath}, generationId={generationId}");
            return (false, ex.Message, null);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempPath))
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                }
            }
        }
    }

    public async Task<(bool Success, string? ErrorMessage)> DeleteGenerationAsync(string projectPath, string generationId, CancellationToken cancellationToken = default)
    {
        var normalizedProjectPath = NormalizeProjectPath(projectPath);
        var projectId = hashService.ComputeProjectId(normalizedProjectPath);
        var generationDirectory = storage.GetGenerationDirectory(projectId, generationId);
        var deletedDirectory = storage.GetDeletedGenerationDirectory(projectId, generationId);

        logger.Info($"GenerationDeleteStarted projectPath={normalizedProjectPath}, generationId={generationId}");

        try
        {
            if (!Directory.Exists(generationDirectory))
            {
                return (false, "世代フォルダが見つかりません。");
            }

            if (Directory.Exists(deletedDirectory))
            {
                Directory.Delete(deletedDirectory, recursive: true);
            }

            await storage.MoveDirectoryAsync(generationDirectory, deletedDirectory, cancellationToken).ConfigureAwait(false);

            var manifest = (await manifestRepository.LoadAsync(projectId, normalizedProjectPath, cancellationToken).ConfigureAwait(false)).Manifest;
            manifest.Generations.RemoveAll(x => string.Equals(x.GenerationId, generationId, StringComparison.OrdinalIgnoreCase));
            manifest.UpdatedAt = DateTimeOffset.Now;
            await manifestRepository.SaveAsync(projectId, manifest, cancellationToken).ConfigureAwait(false);

            logger.Info($"GenerationDeleteCompleted projectPath={normalizedProjectPath}, generationId={generationId}, deletedPath={deletedDirectory}");
            return (true, null);
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"GenerationDeleteFailed projectPath={normalizedProjectPath}, generationId={generationId}");
            try
            {
                if (Directory.Exists(deletedDirectory) && !Directory.Exists(generationDirectory))
                {
                    Directory.Move(deletedDirectory, generationDirectory);
                }
            }
            catch
            {
            }

            return (false, ex.Message);
        }
    }

    private static ProjectGenerationRecord BuildGenerationRecord(ProjectGenerationManifestItem metadata, string generationDirectory)
    {
        return new ProjectGenerationRecord
        {
            GenerationId = metadata.GenerationId,
            CreatedAt = metadata.CreatedAt,
            DisplayName = metadata.DisplayName,
            Memo = metadata.Memo,
            SourceProjectPath = metadata.SourceProjectPath,
            FileSize = metadata.FileSize,
            Sha256 = metadata.Sha256,
            CreatedByVersion = metadata.CreatedByVersion,
            GenerationPath = generationDirectory,
            IsValid = true,
        };
    }

    private static string NormalizeProjectPath(string projectPath)
    {
        return Path.GetFullPath(projectPath);
    }

    private static string GetCreatedByVersion()
    {
        var version = typeof(ProjectGenerationService).Assembly.GetName().Version;
        return version?.ToString() ?? "0.0.0";
    }

    private static string FormatDefaultGenerationName(DateTimeOffset createdAt)
    {
        return createdAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    }

    private static string CreateRestoreAsNewFilePath(string projectPath, string generationId)
    {
        var directory = Path.GetDirectoryName(projectPath) ?? AppContext.BaseDirectory;
        var fileName = Path.GetFileNameWithoutExtension(projectPath);
        var extension = Path.GetExtension(projectPath);
        return Path.Combine(directory, $"{fileName}_{generationId}{extension}");
    }

    private static void TryOpenExclusive(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static async Task<string> CreateValidatedRestoreTempAsync(string sourcePath, string expectedSha256, string targetDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetDirectory);
        var tempPath = Path.Combine(targetDirectory, $"{Guid.NewGuid():N}.restore.tmp");

        await using (var source = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var destination = File.Create(tempPath))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        var tempSha = await Task.Run(() =>
        {
            using var stream = File.OpenRead(tempPath);
            var bytes = SHA256.HashData(stream);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(tempSha, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("一時ファイルの SHA256 検証に失敗しました。");
        }

        return tempPath;
    }
}
