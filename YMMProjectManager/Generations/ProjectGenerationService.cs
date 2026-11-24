using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using System.Threading;
using System.Threading.Tasks;
using YMMProjectManager.Application;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Infrastructure.Generations;

//CSJ,class=ProjectGenerationService,プロジェクトの世代管理
public sealed class ProjectGenerationService : IProjectGenerationService
{
    //CSJ,field=logger,ロガー
    private readonly FileLogger logger;
    //CSJ,field=hashService,ハッシュサービス
    private readonly ProjectGenerationHashService hashService;
    //CSJ,field=createId,作成ID
    private readonly Func<DateTimeOffset, string> createId;
    //CSJ,field=storage,保管
    private readonly ProjectGenerationStorage storage;
    //CSJ,field=manifestRepository,目録リポジトリ
    private readonly ProjectGenerationManifestRepository manifestRepository;
    private readonly Action<string>? beforeRestoreCommit;
    private readonly Action? afterRestoreParking;

    public ProjectGenerationService(FileLogger logger, string? storageRootDirectory = null)
        : this(logger, new ProjectGenerationStorage(storageRootDirectory)) { }

    internal ProjectGenerationService(FileLogger logger, ProjectGenerationStorage storage,
        Func<DateTimeOffset, string>? createId = null, Action<string>? beforeRestoreCommit = null, Action? afterRestoreParking = null)
    {
        this.logger = logger;
        hashService = new ProjectGenerationHashService();
        this.createId = createId ?? new ProjectGenerationIdFactory().Create;
        this.storage = storage;
        this.beforeRestoreCommit = beforeRestoreCommit;
        this.afterRestoreParking = afterRestoreParking;
        manifestRepository = new ProjectGenerationManifestRepository(storage, logger);
    }

    //CSJ,method=GetRootDirectory,ルートディレクトリを取得する
    public string GetRootDirectory() => storage.RootDirectory;

    //CSJ,method=GetProjectDirectory,プロジェクトディレクトリを取得する
    public string GetProjectDirectory(string projectPath) => storage.GetProjectDirectory(hashService.ComputeProjectId(projectPath));

    //CSJ,method=CreateGenerationAsync,新しい世代を保存する
    public async Task<ProjectGenerationRecord> CreateGenerationAsync(string projectPath, string displayName, string? memo, CancellationToken cancellationToken = default)
    {
        //CSJ,variable=normalizedProjectPath,正規化済みプロジェクトのパス
        var normalizedProjectPath = NormalizeProjectPath(projectPath);
        //CSJ,variable=projectId,プロジェクトID
        var projectId = hashService.ComputeProjectId(normalizedProjectPath);
        var createdAt = DateTimeOffset.Now;
        //CSJ,variable=generationId,世代ID
        var generationId = createId(createdAt);
        //CSJ,variable=generationDirectory,世代ディレクトリ
        var generationDirectory = storage.GetGenerationDirectory(projectId, generationId);
        //CSJ,variable=generationFilePath,世代ファイルパス
        var generationFilePath = Path.GetFullPath(storage.GetGenerationFilePath(projectId, generationId));

        //CSJ,variable=manifestLoad,目録読み込み
        var manifestLoad = await manifestRepository.LoadAsync(projectId, normalizedProjectPath, cancellationToken).ConfigureAwait(false);
        //CSJ,variable=manifest,目録
        var manifest = RequireManifest(manifestLoad);
        storage.EnsureProjectLayout(projectId);
        if (manifest.Generations.Count == 0)
        {
            manifest.CreatedAt = createdAt;
        }

        logger.Info($"GenerationCreateStarted projectPath={normalizedProjectPath}, generationId={generationId}");

        if (manifest.Generations.Any(x => string.Equals(x.GenerationId, generationId, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Generation ID already exists in the manifest.");

        var fullGenerationDirectory = Path.GetFullPath(generationDirectory);
        using var generationParent = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(fullGenerationDirectory)!, enforceShare: true);
        using var generation = CreateGenerationDirectory(fullGenerationDirectory);
        FileStream? snapshot = null;
        FileStream? metadataFile = null;
        var retain = false;
        try
        {
            snapshot = OwnedFilePublication.CreateOwnedOutputFile(generationFilePath, readBack: true);
            using (var source = File.Open(normalizedProjectPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await source.CopyToAsync(snapshot, cancellationToken).ConfigureAwait(false);
            await snapshot.FlushAsync(cancellationToken).ConfigureAwait(false);
            snapshot.Flush(flushToDisk: true);
            var copiedSize = snapshot.Length;
            snapshot.Position = 0;
            var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(snapshot, cancellationToken).ConfigureAwait(false));

            //CSJ,variable=metadata,メタデータ
            var metadata = new ProjectGenerationManifestItem
            {
                GenerationId = generationId,
                CreatedAt = createdAt,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? FormatDefaultGenerationName(createdAt) : displayName.Trim(),
                Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim(),
                SourceProjectPath = normalizedProjectPath,
                FileSize = copiedSize,
                Sha256 = sha256,
                CreatedByVersion = GetCreatedByVersion(),
            };

            var metadataPath = Path.GetFullPath(storage.GetGenerationMetadataPath(projectId, generationId));
            metadataFile = OwnedFilePublication.CreateOwnedOutputFile(metadataPath);
            await ProjectGenerationStorage.WriteOwnedMetadataAsync(metadataFile, metadata, cancellationToken).ConfigureAwait(false);

            manifest.ProjectId = projectId;
            manifest.ProjectPath = normalizedProjectPath;
            manifest.ProjectFileName = Path.GetFileName(normalizedProjectPath);
            manifest.UpdatedAt = createdAt;
            manifest.Generations.Add(metadata);
            await manifestRepository.SaveValidatedAsync(projectId, manifest, manifestLoad, cancellationToken).ConfigureAwait(false);
            retain = true;

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
            // A candidate manifest retained for manual recovery may refer to this exact generation.
            retain |= ex is ProjectGenerationManifestPublication.PendingException;
            logger.Error(ex, $"GenerationCreateFailed projectPath={normalizedProjectPath}, generationId={generationId}");
            throw;
        }
        finally
        {
            if (!retain)
            {
                if (metadataFile is not null) OwnedFilePublication.TryDeleteOpenedPath(metadataFile.SafeFileHandle);
                if (snapshot is not null) OwnedFilePublication.TryDeleteOpenedPath(snapshot.SafeFileHandle);
            }

            metadataFile?.Dispose();
            snapshot?.Dispose();
            if (!retain)
                OwnedFilePublication.TryDeleteOpenedPath(generation); // Only succeeds for this exact empty directory.
        }
    }

    private static SafeFileHandle CreateGenerationDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        using var parentHandle = OwnedFilePublication.OpenOrdinaryPath(parent, expectDirectory: true, allowRename: false, FileShare.ReadWrite);
        using var created = OwnedStagingDirectory.CreateAndPin(parentHandle, Path.GetFileName(path));
        using var witness = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: true, allowRename: false,
            FileShare.ReadWrite | FileShare.Delete, enforceDirectoryShare: true);
        var identity = ProjectFileEvidence.ReadPhysicalIdentity(created);
        if (ProjectFileEvidence.ReadPhysicalIdentity(witness) != identity)
            throw new IOException("作成した世代フォルダーを確認できません。");

        created.Dispose();
        var owned = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: true, allowRename: true,
            FileShare.ReadWrite, enforceDirectoryShare: true);
        try
        {
            if (ProjectFileEvidence.ReadPhysicalIdentity(owned) == identity)
                return owned;
            throw new IOException("作成した世代フォルダーが変更されました。");
        }
        catch
        {
            owned.Dispose();
            throw;
        }
    }

    //CSJ,method=GetGenerationsAsync,世代を取得する（非同期）
    public async Task<IReadOnlyList<ProjectGenerationRecord>> GetGenerationsAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        //CSJ,variable=normalizedProjectPath,正規化済みプロジェクトのパス
        var normalizedProjectPath = NormalizeProjectPath(projectPath);
        //CSJ,variable=projectId,プロジェクトID
        var projectId = hashService.ComputeProjectId(normalizedProjectPath);
        //CSJ,variable=manifestLoad,目録読み込み
        var manifestLoad = await manifestRepository.LoadAsync(projectId, normalizedProjectPath, cancellationToken).ConfigureAwait(false);
        //CSJ,variable=manifest,目録
        var manifest = RequireManifest(manifestLoad);
        //CSJ,variable=results,結果一覧
        var results = new List<ProjectGenerationRecord>();

        foreach (var item in manifest.Generations.OrderByDescending(x => x.CreatedAt))
        {
            try
            {
                //CSJ,variable=generationDirectory,世代ディレクトリ
                var generationDirectory = storage.GetGenerationDirectory(projectId, item.GenerationId);
                //CSJ,variable=generationPath,世代パス
                var generationPath = storage.GetGenerationFilePath(projectId, item.GenerationId);
                //CSJ,variable=metadata,メタデータ
                var metadata = await storage.ReadMetadataAsync(projectId, item.GenerationId, cancellationToken).ConfigureAwait(false);
                //CSJ,variable=record,記録
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
                    //CSJ,variable=actualSha,実測値SHA
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

    //CSJ,method=GetDiagnosticsAsync,診断情報を取得する（非同期）
    public async Task<ProjectGenerationDiagnostics> GetDiagnosticsAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        //CSJ,variable=normalizedProjectPath,正規化済みプロジェクトのパス
        var normalizedProjectPath = NormalizeProjectPath(projectPath);
        //CSJ,variable=projectId,プロジェクトID
        var projectId = hashService.ComputeProjectId(normalizedProjectPath);
        //CSJ,variable=manifestLoad,目録読み込み
        var manifestLoad = await manifestRepository.LoadAsync(projectId, normalizedProjectPath, cancellationToken).ConfigureAwait(false);
        //CSJ,variable=generations,世代
        var generations = manifestLoad.Manifest is null
            ? Array.Empty<ProjectGenerationRecord>()
            : await GetGenerationsAsync(projectPath, cancellationToken).ConfigureAwait(false);
        //CSJ,variable=latest,最新
        var latest = generations.OrderByDescending(x => x.CreatedAt).FirstOrDefault(x => x.IsValid) ?? generations.OrderByDescending(x => x.CreatedAt).FirstOrDefault();

        //CSJ,variable=diagnostics,診断情報
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

    //CSJ,method=RestoreGenerationAsync,指定世代から復元する
    public async Task<(bool Success, string? ErrorMessage, ProjectGenerationRecord? Generation)> RestoreGenerationAsync(string projectPath, string generationId, GenerationRestoreMode restoreMode, CancellationToken cancellationToken = default)
    {
        if (!ProjectGenerationStorage.IsValidGenerationId(generationId)) return (false, "世代 ID が不正です。", null);
        //CSJ,variable=normalizedProjectPath,正規化済みプロジェクトのパス
        var normalizedProjectPath = NormalizeProjectPath(projectPath);
        //CSJ,variable=projectId,プロジェクトID
        var projectId = hashService.ComputeProjectId(normalizedProjectPath);
        //CSJ,variable=generationPath,世代パス
        var generationPath = Path.GetFullPath(storage.GetGenerationFilePath(projectId, generationId));

        logger.Info($"GenerationRestoreStarted projectPath={normalizedProjectPath}, generationId={generationId}, mode={restoreMode}");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectDirectory = Path.GetDirectoryName(normalizedProjectPath)!;
            var outputPath = restoreMode == GenerationRestoreMode.RestoreAsNewFile
                ? CreateRestoreAsNewFilePath(normalizedProjectPath, generationId)
                : normalizedProjectPath;

            using var targetFence = OwnedFilePublication.DirectoryFence.Open(projectDirectory, enforceShare: true);
            ProjectGenerationRestoreRecovery.RequireNoPending(outputPath);
            await using var original = restoreMode == GenerationRestoreMode.RestoreAsNewFile
                ? null : ProjectGenerationRestorePublication.OpenTarget(outputPath);
            var generationDirectory = Path.GetDirectoryName(generationPath)!;
            using var generationFence = OwnedFilePublication.DirectoryFence.Open(generationDirectory, enforceShare: true);
            var metadata = await storage.ReadMetadataAsync(projectId, generationId, cancellationToken).ConfigureAwait(false);
            if (metadata is null)
            {
                logger.Info($"GenerationMetadataCorrupted projectId={projectId}, generationId={generationId}, reason=metadata-missing");
                return (false, "metadata.json が見つかりません。", null);
            }

            using var sourceHandle = OwnedFilePublication.OpenOrdinaryPath(generationPath, expectDirectory: false, allowRename: false);
            using var source = new FileStream(sourceHandle, FileAccess.Read);
            var temporaryPath = Path.Combine(projectDirectory, $"{Guid.NewGuid():N}.restore.tmp");
            await using var candidate = ProjectGenerationRestorePublication.CreateCandidate(temporaryPath, original);
            var retain = false;
            try
            {
                if (original is not null)
                    ProjectListReplacement.VerifyCompatibleFileSecurity(original, candidate, allowAclCreationMarkerDifference: true);
                await source.CopyToAsync(candidate, cancellationToken).ConfigureAwait(false);
                await candidate.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (original is not null)
                    File.SetCreationTimeUtc(candidate.SafeFileHandle, File.GetCreationTimeUtc(original.SafeFileHandle));
                candidate.Flush(flushToDisk: true);
                candidate.Position = 0;
                var actualSha = Convert.ToHexString(await SHA256.HashDataAsync(candidate, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(actualSha, metadata.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA256 が一致しません。世代ファイルが破損している可能性があります。");

                var backupPath = original is null ? null : storage.ResolveRestoreBackupPath(projectId, outputPath);
                beforeRestoreCommit?.Invoke(backupPath ?? outputPath);
                var retained = await ProjectGenerationRestorePublication.PublishAsync(candidate, temporaryPath, original, outputPath,
                    backupPath, cancellationToken, afterRestoreParking).ConfigureAwait(false);
                retain = true;
                logger.Info($"GenerationRestoreCompleted projectPath={normalizedProjectPath}, generationId={generationId}, restoredPath={outputPath}, backupPath={backupPath}, retainedOriginalPath={retained}");
                return (true, null, BuildGenerationRecord(metadata, generationDirectory));
            }
            catch (ProjectGenerationRestorePublication.PendingException)
            {
                retain = true;
                throw;
            }
            finally
            {
                if (!retain) OwnedFilePublication.TryDeleteOpenedPath(candidate.SafeFileHandle);
            }
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"GenerationRestoreFailed projectPath={normalizedProjectPath}, generationId={generationId}");
            return (false, ex.Message, null);
        }
    }

    //CSJ,method=DeleteGenerationAsync,世代を削除する（非同期）
    public async Task<(bool Success, string? ErrorMessage)> DeleteGenerationAsync(string projectPath, string generationId, CancellationToken cancellationToken = default)
    {
        if (!ProjectGenerationStorage.IsValidGenerationId(generationId)) return (false, "世代 ID が不正です。");
        //CSJ,variable=normalizedProjectPath,正規化済みプロジェクトのパス
        var normalizedProjectPath = NormalizeProjectPath(projectPath);
        //CSJ,variable=projectId,プロジェクトID
        var projectId = hashService.ComputeProjectId(normalizedProjectPath);
        //CSJ,variable=generationDirectory,世代ディレクトリ
        var generationDirectory = Path.GetFullPath(storage.GetGenerationDirectory(projectId, generationId));
        //CSJ,variable=deletedDirectory,削除済みのディレクトリ
        var deletedDirectory = Path.GetFullPath(storage.GetDeletedGenerationDirectory(projectId, generationId));

        logger.Info($"GenerationDeleteStarted projectPath={normalizedProjectPath}, generationId={generationId}");
        try
        {
            //CSJ,variable=manifestLoad,目録読み込み
            var manifestLoad = await manifestRepository.LoadAsync(projectId, normalizedProjectPath, cancellationToken).ConfigureAwait(false);
            //CSJ,variable=manifest,目録
            var manifest = RequireManifest(manifestLoad);
            var retiring = manifest.Generations.FirstOrDefault(item => string.Equals(item.GenerationId, generationId, StringComparison.OrdinalIgnoreCase));
            if (retiring is null)
                return (false, "世代 manifest に対象がありません。");
            using var sourceFence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(generationDirectory)!, enforceShare: true);
            using var destinationFence = ProjectGenerationStorage.OpenOrCreateOutputDirectory(Path.GetDirectoryName(deletedDirectory)!);
            using var generation = OwnedFilePublication.OpenOrdinaryPath(generationDirectory, expectDirectory: true,
                allowRename: true, share: FileShare.ReadWrite, enforceDirectoryShare: true);
            cancellationToken.ThrowIfCancellationRequested();
            OwnedFilePublication.RenameOpenedPath(generation, deletedDirectory);

            try
            {
                manifest.Generations.RemoveAll(x => string.Equals(x.GenerationId, generationId, StringComparison.OrdinalIgnoreCase));
                manifest.UpdatedAt = DateTimeOffset.Now;
                await manifestRepository.SaveValidatedAsync(projectId, manifest, manifestLoad, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // Roll back the exact retired directory. A new directory at either path must not be moved or overwritten.
                var current = await manifestRepository.LoadAsync(projectId, normalizedProjectPath, CancellationToken.None).ConfigureAwait(false);
                if (current.Manifest?.Generations.Any(item => string.Equals(item.GenerationId, generationId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Sha256, retiring.Sha256, StringComparison.OrdinalIgnoreCase) && item.FileSize == retiring.FileSize) != true)
                    throw new IOException($"世代の削除を確定できませんでした。移動先を保全して manifest と照合してください。\n移動先 {deletedDirectory}", error);

                try { OwnedFilePublication.RenameOpenedPath(generation, generationDirectory); }
                catch (IOException rollbackError)
                {
                    throw new IOException($"世代の削除を確定できず、元の場所にも戻せませんでした。両方のフォルダーを保全して状態を確認してください。\n元の場所 {generationDirectory}\n移動先 {deletedDirectory}",
                        new AggregateException(error, rollbackError));
                }
                throw;
            }

            logger.Info($"GenerationDeleteCompleted projectPath={normalizedProjectPath}, generationId={generationId}, deletedPath={deletedDirectory}");
            return (true, null);
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"GenerationDeleteFailed projectPath={normalizedProjectPath}, generationId={generationId}");
            return (false, ex.Message);
        }
    }

    //CSJ,method=RequireManifest,要求目録
    private static ProjectGenerationManifest RequireManifest(ProjectGenerationManifestLoadResult result)
    {
        return result.Manifest ?? throw new InvalidDataException(
            $"世代 manifest を読み込めません。既存データを保護するため処理を中止しました。状態: {result.Status}\n{result.ErrorMessage}");
    }

    //CSJ,method=BuildGenerationRecord,世代記録を構築する
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

    //CSJ,method=NormalizeProjectPath,プロジェクトのパスを正規化する
    private static string NormalizeProjectPath(string projectPath)
    {
        return Path.GetFullPath(projectPath);
    }

    private static string GetCreatedByVersion()
    {
        //CSJ,variable=version,バージョン
        var version = typeof(ProjectGenerationService).Assembly.GetName().Version;
        return version?.ToString() ?? "0.0.0";
    }

    private static string FormatDefaultGenerationName(DateTimeOffset createdAt)
    {
        return createdAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    }

    private static string CreateRestoreAsNewFilePath(string projectPath, string generationId)
    {
        //CSJ,variable=directory,ディレクトリ
        var directory = Path.GetDirectoryName(projectPath) ?? AppContext.BaseDirectory;
        //CSJ,variable=fileName,ファイル名
        var fileName = Path.GetFileNameWithoutExtension(projectPath);
        //CSJ,variable=extension,拡張子
        var extension = Path.GetExtension(projectPath);
        return Path.Combine(directory, $"{fileName}_{generationId}{extension}");
    }

}
