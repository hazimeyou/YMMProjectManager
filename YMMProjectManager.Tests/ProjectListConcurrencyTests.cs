using System.IO;
using System.Security.Cryptography;
using System.Diagnostics;
using YMMProjectManager.Domain;
using YMMProjectManager.Application;
using YMMProjectManager.Infrastructure;

internal static class ProjectListConcurrencyTests
{
    public static async Task RunAsync(string workRoot)
    {
        var root = Path.Combine(workRoot, "project-list-concurrency"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "projects.json"); var logger = new FileLogger(Path.Combine(root, "test.log"));
        var a = new JsonProjectRepository(logger, path); var b = new JsonProjectRepository(logger, path);
        var first = await a.LoadAsync(); var stale = await b.LoadAsync();
        first.Projects.Add(new ProjectEntry { FullPath = Path.Combine(root, "one.ymmp"), ProjectId = Guid.NewGuid() });
        await a.SaveAsync(first);
        var saved = SHA256.HashData(await File.ReadAllBytesAsync(path));
        stale.Projects.Add(new ProjectEntry { FullPath = Path.Combine(root, "two.ymmp"), ProjectId = Guid.NewGuid() });
        try { await b.SaveAsync(stale); throw new Exception("stale project list was accepted"); }
        catch (ProjectListConflictException) { }
        var after = SHA256.HashData(await File.ReadAllBytesAsync(path));
        if (!saved.SequenceEqual(after) || (await a.LoadAsync()).Projects.Count != 1) throw new Exception("stale write changed existing list");

        Process StartWorker(string id)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--project-list-worker"); start.ArgumentList.Add(root); start.ArgumentList.Add(id);
            return Process.Start(start)!;
        }
        using var one = StartWorker("one"); using var two = StartWorker("two");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(Path.Combine(root, "one.ready")) || !File.Exists(Path.Combine(root, "two.ready")))
            {
                if (one.HasExited || two.HasExited) throw new Exception("worker exited before barrier");
                await Task.Delay(20, timeout.Token);
            }
            await File.WriteAllTextAsync(Path.Combine(root, "start"), "go", timeout.Token);
            await Task.WhenAll(one.WaitForExitAsync(timeout.Token), two.WaitForExitAsync(timeout.Token));
            if (one.ExitCode != 0 || two.ExitCode != 0) throw new Exception("worker failed: " + await one.StandardError.ReadToEndAsync() + await two.StandardError.ReadToEndAsync());
            var results = new[] { await File.ReadAllTextAsync(Path.Combine(root, "one.result")), await File.ReadAllTextAsync(Path.Combine(root, "two.result")) };
            if (results.Count(x => x == "saved") != 1 || results.Count(x => x == "stale" || x == "busy") != 1) throw new Exception("cross-process updates did not yield one winner and one rejection");
            if ((await a.LoadAsync()).Projects.Count != 2) throw new Exception("cross-process updates lost existing registration");
        }
        finally { if (!one.HasExited) one.Kill(true); if (!two.HasExited) two.Kill(true); }
        var externalContent = "{\"Projects\":[],\"Folders\":[]}";
        var externalRepo = new JsonProjectRepository(logger, path, beforeReplace: () => File.WriteAllText(path, externalContent));
        var old = await externalRepo.LoadAsync();
        try { await externalRepo.SaveAsync(old); throw new Exception("external change was overwritten"); }
        catch (ProjectListConflictException) { }
        if (await File.ReadAllTextAsync(path) != externalContent) throw new Exception("external content was not preserved");
        try { await externalRepo.SaveAsync(old); throw new Exception("retry silently accepted stale revision"); }
        catch (ProjectListConflictException) { }
        var candidate = await externalRepo.LoadAsync();
        var id = Guid.NewGuid(); var testPath = Path.Combine(root, "pending.ymmp");
        candidate.Projects.Add(new ProjectEntry { FullPath = testPath });
        var beforeFingerprint = ProjectListRecovery.Fingerprint(candidate);
        ProjectListRecovery.ApplyIdentity(candidate, testPath, id);
        if (candidate.Projects.Single().ProjectId != id || ProjectListRecovery.Fingerprint(candidate) == beforeFingerprint) throw new Exception("identity recovery did not change expected snapshot");
        candidate.Projects.Add(new ProjectEntry { FullPath = Path.Combine(root, "duplicate.ymmp"), ProjectId = id });
        try { ProjectListRecovery.ApplyIdentity(candidate, testPath, id); throw new Exception("duplicate identity accepted"); } catch (InvalidDataException) { }
        var unsafeInputs = new[]
        {
            "{\"Projects\":[],\"Extra\":\"preserve\"}",
            "{\"Projects\":[{\"FullPath\":\"p.ymmp\",\"FutureField\":\"preserve\"}]}",
            "{\"Projects\":[{\"FullPath\":\"\",\"DisplayName\":\"preserve\"}]}",
            "{\"Projects\":[],\"Folders\":[{\"Name\":\"preserve\"}]}",
            "{\"Projects\":[{\"FullPath\":\"p.ymmp\",\"LinkedYmmpFiles\":[{\"FilePath\":\"\"}]}]}",
            "{\"Projects\":[{\"FullPath\":\"p.ymmp\",\"LinkedYmmpFiles\":[{\"FilePath\":\"p.ymmp\",\"Role\":\"FutureRole\"}]}]}",
            "{\"Projects\":[{\"FullPath\":\"p.ymmp\",\"LinkedYmmpFiles\":[{\"Id\":\"11111111-1111-1111-1111-111111111111\",\"FilePath\":\"p.ymmp\",\"Role\":\"Backup, Draft\",\"RegisteredAt\":\"2026-01-01T00:00:00Z\",\"UpdatedAt\":\"2026-01-01T00:00:00Z\",\"Exists\":true}]}]}",
            "{\"Projects\":[],\"Projects\":[]}",
        };
        foreach (var input in unsafeInputs)
        {
            var unsafePath = Path.Combine(root, "unsafe.json"); await File.WriteAllTextAsync(unsafePath, input);
            var unsafeRepo = new JsonProjectRepository(logger, unsafePath); var loaded = await unsafeRepo.LoadAsync();
            if (loaded.IsRecoverySafe) throw new Exception("lossy recovery input was accepted");
            try { ProjectListRecovery.ApplyIdentity(loaded, "p.ymmp", Guid.NewGuid()); throw new Exception("unsafe recovery accepted"); } catch (InvalidDataException) { }
            try { await unsafeRepo.SaveAsync(loaded); throw new Exception("unsafe repository save accepted"); } catch (InvalidDataException) { }
            if (await File.ReadAllTextAsync(unsafePath) != input) throw new Exception("unsafe recovery input changed");
        }
        var mergeBase = new ProjectStore { Revision = new ProjectListRevision("base"), IsRecoverySafe = true };
        mergeBase.Projects.Add(new ProjectEntry { FullPath = Path.Combine(root, "base.ymmp"), ProjectId = Guid.NewGuid(), DisplayName = "base" });
        var local = ProjectListMerge.Snapshot(mergeBase); var remote = ProjectListMerge.Snapshot(mergeBase);
        local.Projects.Add(new ProjectEntry { FullPath = Path.Combine(root, "local.ymmp"), ProjectId = Guid.NewGuid() });
        remote.Projects.Add(new ProjectEntry { FullPath = Path.Combine(root, "remote.ymmp"), ProjectId = Guid.NewGuid() });
        var independent = ProjectListMerge.Merge(mergeBase, local, remote);
        if (independent.Store?.Projects.Count != 3 || independent.Conflicts.Count != 0) throw new Exception("independent additions did not merge");
        local = ProjectListMerge.Snapshot(mergeBase); remote = ProjectListMerge.Snapshot(mergeBase);
        local.Projects[0].DisplayName = "local"; remote.Projects[0].Pinned = true;
        var differentFields = ProjectListMerge.Merge(mergeBase, local, remote);
        if (differentFields.Store?.Projects.Single().DisplayName != "local" || !differentFields.Store.Projects.Single().Pinned) throw new Exception("independent field edits did not merge");
        var timeBase = ProjectListMerge.Snapshot(mergeBase); timeBase.Projects[0].LastAccess = DateTimeOffset.Parse("2026-01-01T10:00:00+09:00");
        var timeLocal = ProjectListMerge.Snapshot(timeBase); var timeRemote = ProjectListMerge.Snapshot(timeBase);
        timeLocal.Projects[0].LastAccess = DateTimeOffset.Parse("2026-01-01T01:00:00+00:00");
        var timeMerge = ProjectListMerge.Merge(timeBase, timeLocal, timeRemote);
        if (timeMerge.Store?.Projects[0].LastAccess?.Offset != TimeSpan.Zero) throw new Exception("timestamp offset change was lost");
        remote.Projects[0].DisplayName = "remote";
        if (ProjectListMerge.Merge(mergeBase, local, remote).Store is not null) throw new Exception("same-field conflict was accepted");
        remote = ProjectListMerge.Snapshot(mergeBase); remote.Projects[0].ProjectId = Guid.NewGuid();
        if (ProjectListMerge.Merge(mergeBase, local, remote).Store is not null) throw new Exception("external project identity change was accepted");
        local = ProjectListMerge.Snapshot(mergeBase); local.Projects.Clear();
        if (ProjectListMerge.Merge(mergeBase, local, remote).Store is not null) throw new Exception("delete versus edit was accepted");
        local = ProjectListMerge.Snapshot(mergeBase); local.Projects.Clear(); remote = ProjectListMerge.Snapshot(mergeBase);
        remote.Projects[0].FullPath = Path.Combine(root, "remote-move.ymmp");
        if (ProjectListMerge.Merge(mergeBase, local, remote).Store is not null) throw new Exception("delete versus remote move was accepted");
        local = ProjectListMerge.Snapshot(mergeBase); local.Projects[0].FullPath = Path.Combine(root, "local-move.ymmp"); remote = ProjectListMerge.Snapshot(mergeBase); remote.Projects.Clear();
        if (ProjectListMerge.Merge(mergeBase, local, remote).Store is not null) throw new Exception("local move versus deletion was accepted");
        local = ProjectListMerge.Snapshot(mergeBase); remote = ProjectListMerge.Snapshot(mergeBase);
        local.Projects.Add(new ProjectEntry { FullPath = Path.Combine(root, "same.ymmp"), ProjectId = Guid.NewGuid() });
        remote.Projects.Add(new ProjectEntry { FullPath = Path.Combine(root, "same.ymmp"), ProjectId = Guid.NewGuid() });
        if (ProjectListMerge.Merge(mergeBase, local, remote).Store is not null) throw new Exception("conflicting additions were accepted");
        local = ProjectListMerge.Snapshot(mergeBase); local.Projects.Add(new ProjectEntry { FullPath = Path.Combine(root, "duplicate-id.ymmp"), ProjectId = mergeBase.Projects[0].ProjectId });
        if (ProjectListMerge.Merge(mergeBase, local, remote).Store is not null) throw new Exception("duplicate project identity was accepted");
        var folder = new ProjectFolder { Name = "folder" }; var folderBase = ProjectListMerge.Snapshot(mergeBase);
        folderBase.Folders.Add(folder); folderBase.Projects[0].FolderId = folder.Id;
        var folderLocal = ProjectListMerge.Snapshot(folderBase); var folderRemote = ProjectListMerge.Snapshot(folderBase);
        folderLocal.Projects[0].FolderId = null; folderLocal.Folders.Clear();
        folderRemote.Projects.Add(new ProjectEntry { FullPath = Path.Combine(root, "external-folder.ymmp"), ProjectId = Guid.NewGuid(), FolderId = folder.Id });
        if (ProjectListMerge.Merge(folderBase, folderLocal, folderRemote).Store is not null) throw new Exception("folder deletion orphaned an external project");
        var orderBase = ProjectListMerge.Snapshot(mergeBase);
        orderBase.Projects.Add(new ProjectEntry { FullPath = Path.Combine(root, "second-order.ymmp"), ProjectId = Guid.NewGuid() });
        var orderLocal = ProjectListMerge.Snapshot(orderBase); var orderRemote = ProjectListMerge.Snapshot(orderBase);
        orderLocal.Projects.Reverse(); orderLocal.Projects.Add(new ProjectEntry { FullPath = Path.Combine(root, "third-order.ymmp"), ProjectId = Guid.NewGuid() });
        if (ProjectListMerge.Merge(orderBase, orderLocal, orderRemote).Store is not null) throw new Exception("local reorder was silently lost");
        orderLocal = ProjectListMerge.Snapshot(orderBase); orderLocal.Projects.Insert(0, new ProjectEntry { FullPath = Path.Combine(root, "inserted.ymmp"), ProjectId = Guid.NewGuid() });
        if (ProjectListMerge.Merge(orderBase, orderLocal, orderRemote).Store is not null) throw new Exception("local insertion order was silently lost");
        var mergePath = Path.Combine(root, "merge-failure.json"); var mergeRepo = new JsonProjectRepository(logger, mergePath);
        var mergeSeed = await mergeRepo.LoadAsync(); mergeSeed.Projects.Add(new ProjectEntry { FullPath = Path.Combine(root, "seed.ymmp"), ProjectId = Guid.NewGuid() }); await mergeRepo.SaveAsync(mergeSeed);
        var mergeBaseline = await mergeRepo.LoadAsync(); var mergeLocal = ProjectListMerge.Snapshot(mergeBaseline);
        mergeLocal.Projects.Add(new ProjectEntry { FullPath = Path.Combine(root, "local-change.ymmp"), ProjectId = Guid.NewGuid() });
        var otherRepo = new JsonProjectRepository(logger, mergePath); var mergeRemote = await otherRepo.LoadAsync();
        mergeRemote.Projects.Add(new ProjectEntry { FullPath = Path.Combine(root, "external-change.ymmp"), ProjectId = Guid.NewGuid() }); await otherRepo.SaveAsync(mergeRemote);
        var mergeLatest = await mergeRepo.LoadAsync(); var recoveryPlan = ProjectListMerge.Merge(mergeBaseline, mergeLocal, mergeLatest);
        if (recoveryPlan.Store is null) throw new Exception("recovery plan unexpectedly conflicts");
        var beforeFailure = SHA256.HashData(await File.ReadAllBytesAsync(mergePath));
        var injected = new JsonProjectRepository(logger, mergePath, beforeReplace: () => throw new IOException("synthetic recovery save failure"));
        try { await injected.SaveAsync(recoveryPlan.Store); throw new Exception("recovery save failure hidden"); } catch (IOException) { }
        var afterFailure = SHA256.HashData(await File.ReadAllBytesAsync(mergePath));
        if (!beforeFailure.SequenceEqual(afterFailure) || mergeLocal.Projects.Count != 2 || (await mergeRepo.LoadAsync()).Projects.Count != 2) throw new Exception("recovery failure lost local or remote state");
    }

    public static async Task RunWorkerAsync(string root, string id)
    {
        var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "YMMProjectManager-tests")) + Path.DirectorySeparatorChar;
        root = Path.GetFullPath(root);
        if (!root.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) || (id != "one" && id != "two")) throw new InvalidOperationException("worker fixture outside isolated test root");
        var repo = new JsonProjectRepository(new FileLogger(Path.Combine(root, id + ".log")), Path.Combine(root, "projects.json"));
        var store = await repo.LoadAsync();
        store.Projects.Add(new ProjectEntry { FullPath = Path.Combine(root, id + "-worker.ymmp"), ProjectId = Guid.NewGuid() });
        await File.WriteAllTextAsync(Path.Combine(root, id + ".ready"), "ready");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!File.Exists(Path.Combine(root, "start"))) await Task.Delay(20, timeout.Token);
        try { await repo.SaveAsync(store, timeout.Token); await File.WriteAllTextAsync(Path.Combine(root, id + ".result"), "saved"); }
        catch (ProjectListConflictException) { await File.WriteAllTextAsync(Path.Combine(root, id + ".result"), "stale"); }
        catch (ProjectListBusyException) { await File.WriteAllTextAsync(Path.Combine(root, id + ".result"), "busy"); }
    }
}
