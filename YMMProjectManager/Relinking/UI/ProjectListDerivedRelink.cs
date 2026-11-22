using System.IO;
using YMMProjectManager.Application;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Presentation.ViewModels;

//CSJ,class=ProjectListViewModel,プロジェクト一覧の表示モデル
public sealed partial class ProjectListViewModel
{
    //CSJ,method=ApplyDerivedRelinkAsync,派生再リンクを適用する（非同期）
    internal async Task ApplyDerivedRelinkAsync(ProjectEntry source, ProjectMetadataAssociation expectedAssociation,
        //CSJ,parameter=derived,派生
        AssetRelinkRecovery.DerivedOutputReceipt derived, CancellationToken token)
    {
        var output = derived.Output;
        var child = output.Revision;
        //CSJ,variable=association,関連付け
        var association = new ProjectMetadataAssociation(derived.SeriesDirectory, child.RevisionId, child.Sha256,
            child.RevisionId, child.Sha256, child.RevisionId, child.Sha256);
        if (!isInitialized || !isProjectListLossless || retainedRecoverySnapshot is not null || HasUnsavedProjectList ||
            !Projects.Contains(source) || source.MetadataAssociation != expectedAssociation ||
            source.ProjectId != derived.SourceRoot.ProjectId || source.ProjectId != derived.SourceParent.ProjectId ||
            child.ProjectId == Guid.Empty || child.ProjectId == source.ProjectId ||
            expectedAssociation.RootRevisionId != derived.SourceRoot.RevisionId ||
            !string.Equals(expectedAssociation.RootSha256, derived.SourceRoot.Sha256, StringComparison.OrdinalIgnoreCase) ||
            expectedAssociation.SelectedRevisionId != derived.SourceParent.RevisionId ||
            !string.Equals(expectedAssociation.SelectedSha256, derived.SourceParent.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(source.FullPath, output.Source, StringComparison.Ordinal) || projectListRevision is null || savedBaseline is null)
            throw new InvalidDataException("元の登録または一覧の未保存状態が変わりました。一覧を確認してから再開してください。");

        //CSJ,variable=fileIdentity,ファイル識別情報
        var fileIdentity = new ProjectFileIdentity(output.OutputIdentity.Volume, output.OutputIdentity.FileId, output.OutputSha256);
        //CSJ,variable=sourceIdentity,移行元の識別情報
        var sourceIdentity = new ProjectFileIdentity(output.SourceIdentity.Volume, output.SourceIdentity.FileId, output.SourceSha256);
        if (!MatchesSource(source))
            throw new InvalidDataException("元の登録ファイルの実体が変わりました。");

        var before = ProjectListMerge.Snapshot(new ProjectStore
        {
            Projects = Projects.ToList(), Folders = folders.ToList(), Revision = projectListRevision, IsRecoverySafe = isProjectListLossless,
        });
        //CSJ,variable=beforeFingerprint,フィンガープリント照合前の処理
        var beforeFingerprint = ProjectListRecovery.Fingerprint(before);
        if (beforeFingerprint != ProjectListRecovery.Fingerprint(savedBaseline))
            throw new InvalidDataException("一覧に別の変更があります。保存内容を確認してから再開してください。");
        var disk = await repository.LoadAsync(token);
        if (!disk.IsRecoverySafe || disk.Projects.Count(MatchesSource) != 1 || CurrentStoreFingerprint() != beforeFingerprint)
            throw new ProjectListConflictException();

        var screenConflicts = before.Projects.Where(ConflictsWithOutput).ToArray();
        var diskConflicts = disk.Projects.Where(ConflictsWithOutput).ToArray();
        if (diskConflicts.Length == 1 && MatchesRegistration(diskConflicts[0]))
        {
            if (screenConflicts.Length == 1 && MatchesRegistration(screenConflicts[0]) &&
                ProjectListRecovery.Fingerprint(disk) == beforeFingerprint)
                return;
            if (screenConflicts.Length == 0)
            {
                var withoutChild = ProjectListMerge.Snapshot(disk);
                withoutChild.Projects.RemoveAll(entry => entry.ProjectId == child.ProjectId);
                if (ProjectListRecovery.Fingerprint(withoutChild) == beforeFingerprint)
                {
                    Adopt(disk, diskConflicts[0]);
                    return;
                }
            }
        }
        if (screenConflicts.Length != 0 || diskConflicts.Length != 0)
            throw new InvalidDataException("派生ID、出力のファイル実体、または登録先が別の項目と競合しました。");
        if (disk.Revision != before.Revision)
            throw new ProjectListConflictException();

        token.ThrowIfCancellationRequested();
        var entry = new ProjectEntry
        {
            FullPath = output.Output,
            ProjectId = child.ProjectId,
            FileIdentity = fileIdentity,
            MetadataAssociation = association,
            FolderId = source.FolderId,
        };
        entry.LinkedYmmpFiles.Add(new LinkedYmmpFile { FilePath = output.Output, Role = YmmpRole.Main, Exists = true, LastCheckedAt = DateTimeOffset.UtcNow });
        var proposed = ProjectListMerge.Snapshot(before);
        proposed.Projects.Add(entry);
        try { await repository.SaveAsync(proposed, token); }
        catch
        {
            if (repository is JsonProjectRepository json && json.HasInterruptedPublication)
                ProjectListPublicationStatus = "中断された一覧保存があります。一覧の保存状況を確認した後、外部素材の再リンク回復を実行してください。";
            throw;
        }
        Adopt(proposed, entry);

        // Keep derived additions outside generic draft replay. Only the external
        // relink recovery callback can revalidate the assets and pinned child series.
        void Adopt(ProjectStore persisted, ProjectEntry added)
        {
            try
            {
                if (CurrentStoreFingerprint() != beforeFingerprint)
                    throw new InvalidDataException("一覧表示が変更されました。");
                Projects.Add(added);
                //CSJ,variable=fingerprint,フィンガープリント
                var fingerprint = ProjectListRecovery.Fingerprint(persisted);
                if (CurrentStoreFingerprint() != fingerprint)
                    throw new InvalidDataException("一覧表示を確定できません。");
                projectListRevision = persisted.Revision;
                isProjectListLossless = persisted.IsRecoverySafe;
                savedBaseline = ProjectListMerge.Snapshot(persisted);
                lastSavedFingerprint = fingerprint;
            }
            catch
            {
                FreezePartialRecovery(before, "派生登録の後に一覧表示を確定できませんでした。");
                throw;
            }
        }

        //CSJ,method=MatchesSource,元ファイルと一致するか確認する
        bool MatchesSource(ProjectEntry existing) => existing.ProjectId == source.ProjectId &&
            existing.FullPath == output.Source && existing.MetadataAssociation == expectedAssociation &&
            (existing.FileIdentity is null || existing.FileIdentity.IsSameFile(sourceIdentity));
        bool ConflictsWithOutput(ProjectEntry existing) => existing.ProjectId == child.ProjectId ||
            string.Equals(existing.FullPath, output.Output, StringComparison.OrdinalIgnoreCase) ||
            (existing.FileIdentity is { } identity && identity.IsSameFile(fileIdentity)) ||
            existing.LinkedYmmpFiles.Any(link => string.Equals(link.FilePath, output.Output, StringComparison.OrdinalIgnoreCase));
        bool MatchesRegistration(ProjectEntry existing) => existing.ProjectId == child.ProjectId &&
            string.Equals(existing.FullPath, output.Output, StringComparison.Ordinal) &&
            existing.FileIdentity == fileIdentity && existing.MetadataAssociation == association;
    }
}
