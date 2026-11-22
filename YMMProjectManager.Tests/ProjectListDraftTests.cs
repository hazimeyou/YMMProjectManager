using System.IO;
using System.Security.Cryptography;
using YMMProjectManager.Application;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;

//CSJ,class=ProjectListDraftTests,プロジェクト一覧下書きテスト
internal static class ProjectListDraftTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    internal static async Task RunAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(workRoot, "project-list-drafts");
        Directory.CreateDirectory(root);
        //CSJ,variable=path,パス
        var path = Path.Combine(root, "projects.json");
        //CSJ,variable=repository,リポジトリ
        var repository = new JsonProjectRepository(new FileLogger(Path.Combine(root, "test.log")), path);
        //CSJ,variable=baseline,基準
        var baseline = await repository.LoadAsync();
        //CSJ,variable=projectPath,プロジェクトのパス
        var projectPath = Path.Combine(root, "source.ymmp");
        await File.WriteAllTextAsync(projectPath, "{}");
        //CSJ,variable=hash,ハッシュ
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(projectPath)));
        //CSJ,variable=association,関連付け
        var association = new ProjectMetadataAssociation(Path.Combine(root, "series"), Guid.NewGuid(), hash,
            Guid.NewGuid(), hash, Guid.NewGuid(), hash);
        //CSJ,variable=project,プロジェクト
        var project = new ProjectEntry { FullPath = projectPath, ProjectId = Guid.NewGuid(), MetadataAssociation = association };
        //CSJ,variable=local,ローカル
        var local = ProjectListMerge.Snapshot(baseline);
        local.Projects.Add(project);
        //CSJ,variable=evidence,証跡
        var evidence = new Dictionary<string, ProjectListDrafts.Evidence> { [projectPath] = new(project.ProjectId.Value, hash) };
        //CSJ,variable=drafts,下書き一覧
        var drafts = new ProjectListDrafts(repository.RecoveryDirectory);
        var writes = await Task.WhenAll(drafts.WriteAsync(baseline, local, evidence), drafts.WriteAsync(baseline, local, evidence));
        if (writes.Distinct().Count() != 2 || File.Exists(path)) throw new Exception("draft writes interfered with each other or the project list");

        var reopened = new ProjectListDrafts(repository.RecoveryDirectory);
        //CSJ,variable=draft,下書き
        var draft = await reopened.ReadAsync(writes[0]);
        //CSJ,variable=plan,計画
        var plan = ProjectListMerge.Merge(draft.Baseline, draft.Local, await repository.LoadAsync());
        if (plan.Store?.Projects.Single().MetadataAssociation != association || draft.Additions[projectPath] != evidence[projectPath])
            throw new Exception("revision association or addition evidence lost after restart");
        await repository.SaveAsync(plan.Store);
        //CSJ,variable=saved,保存済み
        var saved = await repository.LoadAsync();
        if (!saved.IsRecoverySafe || saved.Projects.Single().MetadataAssociation != association)
            throw new Exception("association lost on list round trip");
        var alreadyApplied = ProjectListMerge.Merge(draft.Baseline, draft.Local, saved);
        if (alreadyApplied.Store?.Projects.Count != 1) throw new Exception("reapplication duplicated the recovered project");
        var claim = reopened.Claim(writes[0], Guid.NewGuid());
        try { reopened.Claim(writes[0], Guid.NewGuid()); throw new Exception("same draft claimed twice"); }
        catch (FileNotFoundException) { }
        if ((await reopened.ReadAsync(claim, claimed: true)).Local.Projects.Single().ProjectId != project.ProjectId)
            throw new Exception("interrupted publication evidence cannot be exported");
        reopened.Retire(claim, claimed: true);
        if (File.Exists(writes[0]) || !File.Exists(writes[0] + ".closed") || !File.Exists(writes[1]))
            throw new Exception("explicit retirement affected another draft");
        await Program.RunNamedCaseAsync("Closed recovery artifacts can be read only through archive export", async () =>
        {
            //CSJ,variable=archivePath,アーカイブのパス
            var archivePath = writes[0] + ".closed";
            var historical = await reopened.ReadArchivedAsync(archivePath);
            if (historical.Local.Projects.Single().MetadataAssociation != association)
                throw new Exception("Archive export did not retain the saved association.");
            try { reopened.Claim(archivePath, Guid.NewGuid()); throw new Exception("An archived draft could be claimed for reapplication."); }
            catch (InvalidDataException) { }
        });
        await File.WriteAllTextAsync(writes[1], "{\"Version\":1}");
        try { await reopened.ReadAsync(writes[1]); throw new Exception("invalid record accepted"); }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException) { }
        if ((await repository.LoadAsync()).Projects.Single().ProjectId != project.ProjectId)
            throw new Exception("invalid record changed the saved list");
    }
}
