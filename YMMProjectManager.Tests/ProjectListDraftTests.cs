using System.IO;
using System.Security.Cryptography;
using YMMProjectManager.Application;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;

internal static class ProjectListDraftTests
{
    internal static async Task RunAsync(string workRoot)
    {
        var root = Path.Combine(workRoot, "project-list-drafts");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "projects.json");
        var repository = new JsonProjectRepository(new FileLogger(Path.Combine(root, "test.log")), path);
        var baseline = await repository.LoadAsync();
        var projectPath = Path.Combine(root, "source.ymmp");
        await File.WriteAllTextAsync(projectPath, "{}");
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(projectPath)));
        var association = new ProjectMetadataAssociation(Path.Combine(root, "series"), Guid.NewGuid(), hash,
            Guid.NewGuid(), hash, Guid.NewGuid(), hash);
        var project = new ProjectEntry { FullPath = projectPath, ProjectId = Guid.NewGuid(), MetadataAssociation = association };
        var local = ProjectListMerge.Snapshot(baseline);
        local.Projects.Add(project);
        var evidence = new Dictionary<string, ProjectListDrafts.Evidence> { [projectPath] = new(project.ProjectId.Value, hash) };
        var drafts = new ProjectListDrafts(repository.RecoveryDirectory);
        var writes = await Task.WhenAll(drafts.WriteAsync(baseline, local, evidence), drafts.WriteAsync(baseline, local, evidence));
        if (writes.Distinct().Count() != 2 || File.Exists(path)) throw new Exception("draft writes interfered with each other or the project list");

        var reopened = new ProjectListDrafts(repository.RecoveryDirectory);
        var draft = await reopened.ReadAsync(writes[0]);
        var plan = ProjectListMerge.Merge(draft.Baseline, draft.Local, await repository.LoadAsync());
        if (plan.Store?.Projects.Single().MetadataAssociation != association || draft.Additions[projectPath] != evidence[projectPath])
            throw new Exception("revision association or addition evidence lost after restart");
        await repository.SaveAsync(plan.Store);
        var saved = await repository.LoadAsync();
        if (!saved.IsRecoverySafe || saved.Projects.Single().MetadataAssociation != association)
            throw new Exception("association lost on list round trip");
        var alreadyApplied = ProjectListMerge.Merge(draft.Baseline, draft.Local, saved);
        if (alreadyApplied.Store?.Projects.Count != 1) throw new Exception("reapplication duplicated the recovered project");
        var claim = reopened.Claim(writes[0]);
        try { reopened.Claim(writes[0]); throw new Exception("same draft claimed twice"); }
        catch (FileNotFoundException) { }
        if ((await reopened.ReadAsync(claim, claimed: true)).Local.Projects.Single().ProjectId != project.ProjectId)
            throw new Exception("interrupted publication evidence cannot be exported");
        reopened.Retire(claim, claimed: true);
        if (File.Exists(writes[0]) || !File.Exists(writes[0] + ".closed") || !File.Exists(writes[1]))
            throw new Exception("explicit retirement affected another draft");
        await File.WriteAllTextAsync(writes[1], "{\"Version\":1}");
        try { await reopened.ReadAsync(writes[1]); throw new Exception("invalid record accepted"); }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException) { }
        if ((await repository.LoadAsync()).Projects.Single().ProjectId != project.ProjectId)
            throw new Exception("invalid record changed the saved list");
    }
}
