using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Presentation.ViewModels;

internal static class ProjectListRecoveryTests
{
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    public static async Task RunAsync(string workRoot)
    {
        await Program.WpfTestHost.RunAsync(async () =>
        {
            var root = Path.Combine(workRoot, "recovery"); Directory.CreateDirectory(root);
            var list = Path.Combine(root, "projects.json"); var project = Path.Combine(root, "sample.ymmp");
            await File.WriteAllTextAsync(project, "{}");
            var logger = new FileLogger(Path.Combine(root, "test.log"));
            YMMProjectManager.Domain.ProjectEntry Entry(string path, Guid? id = null)
            {
                var item = new YMMProjectManager.Domain.ProjectEntry { FullPath = path, ProjectId = id };
                item.LinkedYmmpFiles.Add(new YMMProjectManager.Domain.LinkedYmmpFile { FilePath = path, Role = YMMProjectManager.Domain.YmmpRole.Main, Exists = File.Exists(path) });
                return item;
            }
            var initialRepo = new JsonProjectRepository(logger, list); await initialRepo.SaveAsync(await initialRepo.LoadAsync());
            var before = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(list));
            bool fail = true; bool cancel = false; int calls = 0;
            var repo = new JsonProjectRepository(logger, list, beforeReplace: () => { calls++; if (cancel) throw new OperationCanceledException(); if (fail) throw new IOException("synthetic failure"); });
            var vm = new ProjectListViewModel(logger, repo);
            await vm.InitializeAsync();
            var threadId = GetCurrentThreadId(); int dialogs = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) => EnumThreadWindows(threadId, (hwnd, _) => { var name = new StringBuilder(128); GetClassName(hwnd, name, name.Capacity); if (name.ToString() == "#32770") { dialogs++; PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero); } return true; }, IntPtr.Zero);
            timer.Start();
            try { await vm.AddProjectsAsync([project]); } finally { timer.Stop(); }
            var afterFailure = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(list));
            if (dialogs == 0 || vm.Projects.Count != 1 || !before.SequenceEqual(afterFailure) ) throw new Exception("failure notification or preservation not observed");
            fail = false;
            if (!vm.HasUnsavedProjectList || !vm.RetryProjectListSaveCommand.CanExecute(null)) throw new Exception("pending state missing");
            await vm.AddProjectsAsync([project]);
            if (calls != 1 || vm.Projects.Count != 1) throw new Exception("duplicate add unexpectedly changed state");
            await vm.RetryProjectListSaveAsync();
            var reloaded = await new JsonProjectRepository(logger, list).LoadAsync();
            if (calls != 2 || vm.HasUnsavedProjectList || reloaded.Projects.Count != 1 || reloaded.Projects[0].FullPath != project) throw new Exception("retry failed");
            // A failed removal keeps the change in memory and can be saved without repeating removal.
            fail = true; var priorDialogs = dialogs; timer.Start();
            try { await (Task)typeof(ProjectListViewModel).GetMethod("RemoveAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(vm, null)!; } finally { timer.Stop(); }
            if (dialogs == priorDialogs || vm.Projects.Count != 0 || !vm.HasUnsavedProjectList || (await repo.LoadAsync()).Projects.Count != 1) throw new Exception("remove failure state");
            fail = false; await vm.RetryProjectListSaveAsync();
            if (vm.HasUnsavedProjectList || (await repo.LoadAsync()).Projects.Count != 0) throw new Exception("remove retry");
            // The extracted-project registration must propagate its save exception to its caller.
            var extracted = Path.Combine(root, "extracted.ymmp"); await File.WriteAllTextAsync(extracted, "{}"); fail = true;
            try { await (Task)typeof(ProjectListViewModel).GetMethod("RegisterExtractedProjectAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(vm, [extracted])!; throw new Exception("registration swallowed failure"); } catch (IOException) { }
            if (!vm.HasUnsavedProjectList || (await repo.LoadAsync()).Projects.Count != 0) throw new Exception("registration failure state");
            fail = false; await vm.RetryProjectListSaveAsync();
            if ((await repo.LoadAsync()).Projects.Single().FullPath != extracted || vm.HasUnsavedProjectList) throw new Exception("registration retry");
            // Cancellation is not shown as an I/O error and remains retryable.
            cancel = true; var extra = Path.Combine(root,"cancelled.ymmp"); await File.WriteAllTextAsync(extra,"{}"); priorDialogs=dialogs; timer.Start();
            try { await vm.AddProjectsAsync([extra]); } finally { timer.Stop(); }
            if (dialogs != priorDialogs || !vm.HasUnsavedProjectList || (await repo.LoadAsync()).Projects.Count != 1) throw new Exception("cancellation behavior");
            cancel = false; await vm.RetryProjectListSaveAsync();
            if (vm.HasUnsavedProjectList || (await repo.LoadAsync()).Projects.Count != 2) throw new Exception("cancellation retry");
            var legacyPath = Path.Combine(root, "legacy-list.json");
            var legacyRepo = new JsonProjectRepository(logger, legacyPath);
            var legacyStore = await legacyRepo.LoadAsync();
            legacyStore.Projects.Add(Entry(project));
            await legacyRepo.SaveAsync(legacyStore);
            var legacyVm = new ProjectListViewModel(logger, legacyRepo); await legacyVm.InitializeAsync();
            legacyVm.SelectedProject = legacyVm.Projects.Single();
            var projectBefore = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(project));
            var dialogReply = 7;
            var confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            confirmTimer.Tick += (_, _) => EnumThreadWindows(threadId, (hwnd, _) => { var name = new StringBuilder(128); GetClassName(hwnd, name, name.Capacity); if (name.ToString() == "#32770") PostMessage(hwnd, 0x0111, (IntPtr)dialogReply, IntPtr.Zero); return true; }, IntPtr.Zero);
            async Task Migrate() => await (Task)typeof(ProjectListViewModel).GetMethod("AssignLegacyProjectIdAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(legacyVm, null)!;
            confirmTimer.Start(); try { await Migrate(); } finally { confirmTimer.Stop(); }
            if ((await legacyRepo.LoadAsync()).Projects.Single().ProjectId is not null) throw new Exception("cancelled legacy migration changed identity");
            dialogReply = 6; confirmTimer.Start(); try { await Migrate(); } finally { confirmTimer.Stop(); }
            var migrated = (await legacyRepo.LoadAsync()).Projects.Single();
            var projectAfter = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(project));
            if (migrated.ProjectId is null || migrated.ProjectId == Guid.Empty || !projectBefore.SequenceEqual(projectAfter)) throw new Exception("legacy identity migration failed or modified project");
            var associatePath = Path.Combine(root, "associate-list.json");
            var associateRepo = new JsonProjectRepository(logger, associatePath);
            var associateStore = await associateRepo.LoadAsync();
            associateStore.Projects.Add(Entry(extracted, migrated.ProjectId));
            associateStore.Projects.Add(Entry(project));
            await associateRepo.SaveAsync(associateStore);
            var associateVm = new ProjectListViewModel(logger, associateRepo); await associateVm.InitializeAsync();
            associateVm.SelectedProject = associateVm.Projects.Last();
            var duplicateManifest = AssetManifestStore.Create(project, [], DateTimeOffset.UtcNow, projectId: migrated.ProjectId);
            try { await associateVm.AssociateLegacyMetadataAsync(associateVm.SelectedProject, duplicateManifest); throw new Exception("duplicate metadata association accepted"); }
            catch (InvalidDataException) { }
            if ((await associateRepo.LoadAsync()).Projects.Last().ProjectId is not null) throw new Exception("rejected metadata association modified list");
            var uniqueManifest = AssetManifestStore.Create(project, [], DateTimeOffset.UtcNow, projectId: Guid.NewGuid());
            await associateVm.AssociateLegacyMetadataAsync(associateVm.SelectedProject, uniqueManifest);
            if ((await associateRepo.LoadAsync()).Projects.Last().ProjectId != uniqueManifest.ProjectId) throw new Exception("metadata association not persisted");
            var failedPath = Path.Combine(root, "associate-failure.json");
            var failedSeed = new JsonProjectRepository(logger, failedPath); var failedStore = await failedSeed.LoadAsync();
            failedStore.Projects.Add(Entry(project)); await failedSeed.SaveAsync(failedStore);
            var failAssociation = true;
            var failedRepo = new JsonProjectRepository(logger, failedPath, beforeReplace: () => { if (failAssociation) throw new IOException("synthetic association failure"); });
            var failedVm = new ProjectListViewModel(logger, failedRepo); await failedVm.InitializeAsync(); failedVm.SelectedProject = failedVm.Projects.Single();
            var failedManifest = AssetManifestStore.Create(project, [], DateTimeOffset.UtcNow, projectId: Guid.NewGuid());
            try { await failedVm.AssociateLegacyMetadataAsync(failedVm.SelectedProject, failedManifest); throw new Exception("association save failure hidden"); }
            catch (IOException) { }
            if (!failedVm.HasUnsavedProjectList || failedVm.SelectedProject.ProjectId != failedManifest.ProjectId || (await failedRepo.LoadAsync()).Projects.Single().ProjectId is not null)
                throw new Exception("association failure did not preserve both states");
            failAssociation = false; await failedVm.RetryProjectListSaveAsync();
            if (failedVm.HasUnsavedProjectList || (await failedRepo.LoadAsync()).Projects.Single().ProjectId != failedManifest.ProjectId) throw new Exception("association retry failed");
            var conflictPath = Path.Combine(root, "associate-conflict.json");
            var conflictRepo = new JsonProjectRepository(logger, conflictPath); var conflictSeed = await conflictRepo.LoadAsync();
            conflictSeed.Projects.Add(Entry(project)); await conflictRepo.SaveAsync(conflictSeed);
            var conflictVm = new ProjectListViewModel(logger, conflictRepo); await conflictVm.InitializeAsync(); conflictVm.SelectedProject = conflictVm.Projects.Single();
            var outsideRepo = new JsonProjectRepository(logger, conflictPath); var outside = await outsideRepo.LoadAsync();
            var outsideEntry = new YMMProjectManager.Domain.ProjectEntry { FullPath = extracted, ProjectId = Guid.NewGuid() };
            outsideEntry.LinkedYmmpFiles.Add(new YMMProjectManager.Domain.LinkedYmmpFile { FilePath = extracted, Role = YMMProjectManager.Domain.YmmpRole.Main, Exists = true });
            outside.Projects.Add(outsideEntry); await outsideRepo.SaveAsync(outside);
            var conflictManifest = AssetManifestStore.Create(project, [], DateTimeOffset.UtcNow, projectId: Guid.NewGuid());
            try { await conflictVm.AssociateLegacyMetadataAsync(conflictVm.SelectedProject, conflictManifest); throw new Exception("stale association accepted"); }
            catch (YMMProjectManager.Application.ProjectListConflictException) { }
            if (!conflictVm.HasUnsavedProjectList || (await conflictRepo.LoadAsync()).Projects.Count != 2) throw new Exception("association conflict lost pending or external data");
            timer.Start(); try { await conflictVm.RetryProjectListSaveAsync(); } finally { timer.Stop(); }
            if (!conflictVm.HasUnsavedProjectList || (await conflictRepo.LoadAsync()).Projects.Count != 2) throw new Exception("association conflict retry overwrote external data");
            async Task Recover() => await (Task)typeof(ProjectListViewModel).GetMethod("RecoverIdentityAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(conflictVm, null)!;
            dialogReply = 7; confirmTimer.Start(); try { await Recover(); } finally { confirmTimer.Stop(); }
            if (!conflictVm.HasUnsavedProjectList || (await conflictRepo.LoadAsync()).Projects.First().ProjectId is not null) throw new Exception("cancelled conflict recovery changed data");
            await File.WriteAllTextAsync(project, "{\"modified\":true}");
            timer.Start(); try { await Recover(); } finally { timer.Stop(); }
            if (!conflictVm.HasUnsavedProjectList || (await conflictRepo.LoadAsync()).Projects.First().ProjectId is not null) throw new Exception("changed project was associated during recovery");
            await File.WriteAllTextAsync(project, "{}");
            dialogReply = 6; confirmTimer.Start(); try { await Recover(); } finally { confirmTimer.Stop(); }
            var recovered = await conflictRepo.LoadAsync();
            if (conflictVm.HasUnsavedProjectList || recovered.Projects.Count != 2 || recovered.Projects.First().ProjectId != conflictManifest.ProjectId || recovered.Projects.Last().FullPath != extracted)
                throw new Exception("identity recovery lost pending or external change: " + conflictVm.AssetStatus + "; pending=" + conflictVm.HasUnsavedProjectList + "; first=" + recovered.Projects.First().ProjectId + "; safe=" + recovered.IsRecoverySafe);
            var generalPath = Path.Combine(root, "general-recovery.json"); var generalRepo = new JsonProjectRepository(logger, generalPath);
            var generalSeed = await generalRepo.LoadAsync(); generalSeed.Projects.Add(Entry(project, Guid.NewGuid())); await generalRepo.SaveAsync(generalSeed);
            var generalVm = new ProjectListViewModel(logger, generalRepo); await generalVm.InitializeAsync();
            var generalOutside = new JsonProjectRepository(logger, generalPath); var generalExternal = await generalOutside.LoadAsync();
            generalExternal.Projects.Add(Entry(extracted, Guid.NewGuid())); await generalOutside.SaveAsync(generalExternal);
            var localFile = Path.Combine(root, "local-add.ymmp"); await File.WriteAllTextAsync(localFile, "{}");
            timer.Start(); try { await generalVm.AddProjectsAsync([localFile]); } finally { timer.Stop(); }
            if (!generalVm.HasUnsavedProjectList || (await generalRepo.LoadAsync()).Projects.Count != 2) throw new Exception("general recovery setup failed");
            async Task RecoverGeneral() => await (Task)typeof(ProjectListViewModel).GetMethod("RecoverProjectListAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(generalVm, null)!;
            dialogReply = 7; confirmTimer.Start(); try { await RecoverGeneral(); } finally { confirmTimer.Stop(); }
            if (!generalVm.HasUnsavedProjectList || (await generalRepo.LoadAsync()).Projects.Count != 2) throw new Exception("cancelled general recovery changed disk");
            await File.WriteAllTextAsync(localFile, "{\"changed\":true}");
            timer.Start(); try { await RecoverGeneral(); } finally { timer.Stop(); }
            if (!generalVm.HasUnsavedProjectList || (await generalRepo.LoadAsync()).Projects.Count != 2) throw new Exception("changed added project was recovered");
            await File.WriteAllTextAsync(localFile, "{}");
            dialogReply = 6; confirmTimer.Start(); try { await RecoverGeneral(); } finally { confirmTimer.Stop(); }
            var generalResult = await generalRepo.LoadAsync();
            if (generalVm.HasUnsavedProjectList || generalResult.Projects.Count != 3 || !generalResult.Projects.Any(x => x.FullPath == localFile) || !generalResult.Projects.Any(x => x.FullPath == extracted))
                throw new Exception("general recovery failed to preserve independent changes");
            var reentrantPath = Path.Combine(root, "reentrant-recovery.json"); var reentrantRepo = new JsonProjectRepository(logger, reentrantPath);
            var reentrantSeed = await reentrantRepo.LoadAsync(); reentrantSeed.Projects.Add(Entry(project, Guid.NewGuid())); await reentrantRepo.SaveAsync(reentrantSeed);
            var reentrantVm = new ProjectListViewModel(logger, reentrantRepo); await reentrantVm.InitializeAsync();
            var reentrantOutsideRepo = new JsonProjectRepository(logger, reentrantPath); var reentrantOutside = await reentrantOutsideRepo.LoadAsync();
            reentrantOutside.Projects.Add(Entry(extracted, Guid.NewGuid())); await reentrantOutsideRepo.SaveAsync(reentrantOutside);
            timer.Start(); try { await reentrantVm.AddProjectsAsync([localFile]); } finally { timer.Stop(); }
            var mutated = false;
            reentrantVm.Projects.CollectionChanged += (_, args) =>
            {
                if (!mutated && args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                {
                    mutated = true;
                    reentrantVm.Projects.Add(Entry(Path.Combine(root, "observer.ymmp"), Guid.NewGuid()));
                }
            };
            async Task RecoverReentrant() => await (Task)typeof(ProjectListViewModel).GetMethod("RecoverProjectListAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(reentrantVm, null)!;
            dialogReply = 6; confirmTimer.Start(); try { await RecoverReentrant(); } finally { confirmTimer.Stop(); }
            if (!mutated || !reentrantVm.HasUnsavedProjectList || !reentrantVm.ExportRecoverySnapshotCommand.CanExecute(null) || !reentrantVm.ExportRecoveryScreenCommand.CanExecute(null) || reentrantVm.AddCommand.CanExecute(null) || reentrantVm.RetryProjectListSaveCommand.CanExecute(null) || (await reentrantRepo.LoadAsync()).Projects.Count != 3)
                throw new Exception("reentrant UI update was reported as successful or lost retained snapshot");
            var frozenCount = reentrantVm.Projects.Count; await reentrantVm.AddProjectsAsync([extracted]);
            if (reentrantVm.Projects.Count != frozenCount) throw new Exception("partial recovery allowed new list mutation");
            var baselinePath = Path.Combine(root, "baseline-conflict.json");
            var baselineRepo = new JsonProjectRepository(logger, baselinePath); var baselineStore = await baselineRepo.LoadAsync();
            baselineStore.Projects.Add(Entry(project)); await baselineRepo.SaveAsync(baselineStore);
            var baselineVm = new ProjectListViewModel(logger, baselineRepo); await baselineVm.InitializeAsync(); baselineVm.SelectedProject = baselineVm.Projects.Single();
            baselineVm.SelectedProject.DisplayName = "unsaved name";
            var baselineOutside = new JsonProjectRepository(logger, baselinePath); var newOutside = await baselineOutside.LoadAsync();
            newOutside.Projects.Add(new YMMProjectManager.Domain.ProjectEntry { FullPath = extracted, ProjectId = Guid.NewGuid() }); await baselineOutside.SaveAsync(newOutside);
            var baselineManifest = AssetManifestStore.Create(project, [], DateTimeOffset.UtcNow, projectId: Guid.NewGuid());
            try { await baselineVm.AssociateLegacyMetadataAsync(baselineVm.SelectedProject, baselineManifest); throw new Exception("baseline conflict missed"); }
            catch (YMMProjectManager.Application.ProjectListConflictException) { }
            if (baselineVm.RecoverIdentityCommand.CanExecute(null) || baselineVm.SelectedProject.DisplayName != "unsaved name") throw new Exception("unrelated unsaved change made recoverable or was lost");
            var duringPath = Path.Combine(root, "during-save.json"); var duringSeed = new JsonProjectRepository(logger, duringPath); var duringStore = await duringSeed.LoadAsync();
            duringStore.Projects.Add(Entry(project)); await duringSeed.SaveAsync(duringStore);
            ProjectListViewModel? duringVm = null;
            var duringRepo = new JsonProjectRepository(logger, duringPath, beforeReplace: () => duringVm!.SelectedProject!.DisplayName = "changed during save");
            duringVm = new ProjectListViewModel(logger, duringRepo); await duringVm.InitializeAsync(); duringVm.SelectedProject = duringVm.Projects.Single();
            await duringVm.AssociateLegacyMetadataAsync(duringVm.SelectedProject, AssetManifestStore.Create(project, [], DateTimeOffset.UtcNow, projectId: Guid.NewGuid()));
            if (!duringVm.HasUnsavedProjectList || duringVm.SelectedProject.DisplayName != "changed during save") throw new Exception("save-time change was not preserved as unsaved");
            var r9Path = Path.Combine(root, "r9-empty-links.json"); var r9Repo = new JsonProjectRepository(logger, r9Path); var r9Store = await r9Repo.LoadAsync();
            r9Store.Projects.Add(new YMMProjectManager.Domain.ProjectEntry { FullPath = project }); await r9Repo.SaveAsync(r9Store);
            var r9Vm = new ProjectListViewModel(logger, r9Repo); await r9Vm.InitializeAsync(); r9Vm.SelectedProject = r9Vm.Projects.Single();
            var r9Manifest = AssetManifestStore.Create(project, [], DateTimeOffset.UtcNow, projectId: Guid.NewGuid());
            await r9Vm.AssociateLegacyMetadataAsync(r9Vm.SelectedProject, r9Manifest);
            var r9After = await r9Repo.LoadAsync();
            if (r9After.Projects.Single().ProjectId != r9Manifest.ProjectId || r9After.Projects.Single().LinkedYmmpFiles.Count != 1) throw new Exception("R9 empty-links project was not safely migrated");
            var unsafeListPath = Path.Combine(root, "unknown-list.json");
            var unknownJson = "{\"Projects\":[{\"FullPath\":" + System.Text.Json.JsonSerializer.Serialize(project) + "}],\"FutureField\":\"keep\"}";
            await File.WriteAllTextAsync(unsafeListPath, unknownJson);
            var unsafeRepo = new JsonProjectRepository(logger, unsafeListPath); var unsafeVm = new ProjectListViewModel(logger, unsafeRepo);
            await unsafeVm.InitializeAsync(); unsafeVm.SelectedProject = unsafeVm.Projects.Single();
            try { await unsafeVm.AssociateLegacyMetadataAsync(unsafeVm.SelectedProject, AssetManifestStore.Create(project, [], DateTimeOffset.UtcNow, projectId: Guid.NewGuid())); throw new Exception("unsafe list association accepted"); }
            catch (InvalidDataException) { }
            timer.Start(); try { await unsafeVm.AddProjectsAsync([extracted]); } finally { timer.Stop(); }
            if (!unsafeVm.HasUnsavedProjectList || await File.ReadAllTextAsync(unsafeListPath) != unknownJson) throw new Exception("unsafe project list was overwritten");
            foreach (var content in new[] { "{invalid", "" })
            {
                var broken = Path.Combine(root, content.Length == 0 ? "empty.json" : "broken.json");
                await File.WriteAllTextAsync(broken, content); var brokenHash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(broken));
                var brokenVm = new ProjectListViewModel(logger, new JsonProjectRepository(logger, broken)); priorDialogs=dialogs; timer.Start();
                try { await brokenVm.InitializeAsync(); await brokenVm.AddProjectsAsync([project]); } finally { timer.Stop(); }
                var brokenAfter = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(broken));
                if (dialogs < priorDialogs + 2 || brokenVm.AddCommand.CanExecute(null) || brokenVm.Projects.Count != 0 || !brokenHash.SequenceEqual(brokenAfter)) throw new Exception("failed load protection");
            }
            var lockedPath = Path.Combine(root, "locked.json"); var lockedRepo = new JsonProjectRepository(logger, lockedPath); await lockedRepo.SaveAsync(await lockedRepo.LoadAsync());
            var lockedHash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(lockedPath));
            var lockedVm = new ProjectListViewModel(logger, new JsonProjectRepository(logger, lockedPath)); priorDialogs=dialogs;
            using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                timer.Start(); try { await lockedVm.InitializeAsync(); } finally { timer.Stop(); }
            }
            var unlockedHash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(lockedPath));
            if (dialogs == priorDialogs || lockedVm.AddCommand.CanExecute(null) || !lockedHash.SequenceEqual(unlockedHash)) throw new Exception("locked load protection");
            Console.WriteLine("R1-A2 independent WPF: add/remove/registration/cancellation and save-only retry passed.");
            Console.WriteLine("R1-A2 isolated original SHA-256 before/after failed add: " + Convert.ToHexString(before) + " / " + Convert.ToHexString(afterFailure));
            Console.WriteLine("R1-A2 independent WPF: invalid, empty and locked load protection passed.");
            return true;
        });
    }
}
