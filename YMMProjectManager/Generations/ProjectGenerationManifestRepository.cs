using System;
using System.IO;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Infrastructure.Generations;

//CSJ,class=ProjectGenerationManifestRepository,世代目録の入出力
public sealed class ProjectGenerationManifestRepository
{
    //CSJ,field=storage,保管
    private readonly ProjectGenerationStorage storage;
    //CSJ,field=logger,ロガー
    private readonly FileLogger logger;

    public ProjectGenerationManifestRepository(ProjectGenerationStorage storage, FileLogger logger)
    {
        this.storage = storage;
        this.logger = logger;
    }

    //CSJ,method=LoadAsync,読み込む（非同期）
    public async Task<ProjectGenerationManifestLoadResult> LoadAsync(string projectId, string projectPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var (manifest, version) = await storage.ReadManifestSnapshotAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (manifest is not null)
            {
                logger.Info($"GenerationManifestLoaded projectId={projectId}, projectPath={projectPath}");
                return new ProjectGenerationManifestLoadResult(manifest, ProjectGenerationManifestStatus.Valid, contentHash: version!.Sha256) { FileVersion = version };
            }
            if (storage.HasExistingGenerations(projectId))
                throw new InvalidDataException("manifest が見つかりませんが、既存の世代データが残っています。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.Error(ex, $"GenerationManifestLoadFailed projectId={projectId}");
            //CSJ,variable=status,状態
            var status = ex is NotSupportedException ? ProjectGenerationManifestStatus.UnsupportedSchema : ProjectGenerationManifestStatus.Corrupted;
            return new ProjectGenerationManifestLoadResult(null, status, ex.Message);
        }

        logger.Info($"GenerationManifestLoaded projectId={projectId}, projectPath={projectPath}, status=Missing");
        return new ProjectGenerationManifestLoadResult(CreateEmptyManifest(projectId, projectPath), ProjectGenerationManifestStatus.Missing);
    }

    //CSJ,method=SaveAsync,保存する（非同期）
    public async Task SaveAsync(string projectId, ProjectGenerationManifest manifest, CancellationToken cancellationToken = default)
    {
        var current = await LoadAsync(projectId, manifest.ProjectPath, cancellationToken).ConfigureAwait(false);
        if (current.Manifest is null)
            throw new InvalidDataException("世代 manifest を読み込めないため保存できません。");
        await SaveValidatedAsync(projectId, manifest, current, cancellationToken).ConfigureAwait(false);
    }

    internal async Task SaveValidatedAsync(string projectId, ProjectGenerationManifest manifest,
        //CSJ,parameter=initial,初期
        ProjectGenerationManifestLoadResult initial, CancellationToken cancellationToken)
    {
        try
        {
            if (initial.Status != ProjectGenerationManifestStatus.Valid && initial.Status != ProjectGenerationManifestStatus.Missing)
                throw new InvalidDataException("世代 manifest を読み込めないため保存できません。");
            if (initial.Status == ProjectGenerationManifestStatus.Valid &&
                (initial.FileVersion is null || initial.FileVersion.Sha256 != initial.ContentHash))
                throw new InvalidDataException("世代 manifest の読み込み時の版を確認できません。");
            await storage.WriteManifestIfUnchangedAsync(projectId, manifest, initial.FileVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Failed to save generation manifest. projectId={projectId}");
            throw;
        }
    }

    //CSJ,method=CreateEmptyManifest,空目録を作成する
    private static ProjectGenerationManifest CreateEmptyManifest(string projectId, string projectPath)
    {
        var now = DateTimeOffset.Now;
        return new ProjectGenerationManifest
        {
            ProjectId = projectId,
            ProjectPath = projectPath,
            ProjectFileName = Path.GetFileName(projectPath),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
