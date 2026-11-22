using System.IO;
using System.Security.Cryptography;
using YMMProjectManager.Application;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Infrastructure;

//CSJ,class=ProjectListRecoveryFiles,プロジェクト一覧復旧ファイル一覧
internal sealed class ProjectListRecoveryFiles : IDisposable
{
    //CSJ,field=files,ファイル一覧
    private readonly List<FileStream> files = [];

    //CSJ,method=OpenAsync,開く（非同期）
    internal static async Task<ProjectListRecoveryFiles> OpenAsync(ProjectStore final, ProjectStore latest,
        //CSJ,parameter=findEvidence,検索証跡
        Func<string, (Guid Id, string Hash)?> findEvidence, CancellationToken token = default)
    {
        //CSJ,variable=existing,既存
        var existing = latest.Projects.ToDictionary(project => project.FullPath, StringComparer.OrdinalIgnoreCase);
        var held = new ProjectListRecoveryFiles();
        try
        {
            //CSJ,variable=project,プロジェクト
            foreach (var project in final.Projects)
            {
                if (existing.TryGetValue(project.FullPath, out var before) &&
                    before.ProjectId == project.ProjectId && before.FileIdentity == project.FileIdentity) continue;
                token.ThrowIfCancellationRequested();
                var expected = findEvidence(project.FullPath);
                //CSJ,variable=identity,識別情報
                var identity = project.FileIdentity;
                if (project.ProjectId is not Guid id ||
                    (identity is null && (expected is null || expected.Value.Id != id)) ||
                    (expected is { } evidence && (evidence.Id != id || (identity is not null && evidence.Hash != identity.Sha256))))
                    throw Refused(project);
                //CSJ,variable=file,ファイル
                var file = ProjectFileEvidence.OpenRead(project.FullPath);
                held.files.Add(file);
                if (identity is not null)
                {
                    if (await ProjectFileEvidence.ReadAsync(file, token).ConfigureAwait(false) != identity) throw Refused(project);
                }
                else if (expected!.Value.Hash != Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)))
                    throw Refused(project);
            }
            return held;
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }

    private static InvalidDataException Refused(ProjectEntry project) =>
        new($"追加・移動先のファイルを確認できません。{project.FullPath}");

    //CSJ,method=Dispose,破棄する
    public void Dispose()
    {
        //CSJ,variable=file,ファイル
        foreach (var file in files) file.Dispose();
    }
}
