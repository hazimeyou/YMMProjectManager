using System.IO;
using System.Reflection;
using YMMProjectManager.Application;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Git;
using YMMProjectManager.Presentation.ViewModels;
using YMMProjectManager.Presentation.Views;

internal static class OfflineControlsTests
{
    public static async Task RunAsync(string workRoot)
    {
        await Program.WpfTestHost.RunAsync(async () =>
        {
            var root = Path.Combine(workRoot, "offline-controls"); Directory.CreateDirectory(root);
            var project = Path.Combine(root, "project.ymmp");
            await File.WriteAllTextAsync(project, "{\"Items\":[{\"FilePath\":\"sample.png\"}],\"Other\":{\"FilePath\":\"note.txt\"}}");
            await File.WriteAllTextAsync(Path.Combine(root, "sample.png"), "synthetic");
            await File.WriteAllTextAsync(Path.Combine(root, "sample2.png"), "second");
            var git = new GitRepository(root);
            if (!(await git.InitializeAsync()).Success) throw new Exception("git init failed");
            var logger = new FileLogger(Path.Combine(root, "test.log"));
            var vm = new ProjectListViewModel(logger, new JsonProjectRepository(logger, Path.Combine(root, "project-list.json")));
            await vm.InitializeAsync();
            vm.SelectedProject = new ProjectEntry { FullPath = project };
            if (!vm.OpenSelectedProjectRelinkCommand.CanExecute(null)) throw new Exception("selected project relink unavailable");
            if (!vm.FetchGitCommand.CanExecute(null) || vm.PullGitCommand.CanExecute(null) ||
                !vm.InspectGitPullCommand.CanExecute(null) || !vm.PushGitCommand.CanExecute(null) || !vm.GitCloneCommand.CanExecute(null))
                throw new Exception("trusted Git controls or Pull fail-closed gate are incorrect");
            if (!vm.RepairProjectListIdentitiesCommand.CanExecute(null))
                throw new Exception("physical identity review is unavailable for a readable project list");
            if (!vm.ExtractBundleCommand.CanExecute(null)) throw new Exception("managed extraction command unavailable");
            async Task Invoke(string name, params object[] args) => await (Task)typeof(ProjectListViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, args)!;
            await Invoke("ShowGitStatusAsync", false);
            if (!vm.GitStatus.Contains("Git:") || !vm.GitStatus.Contains("project.ymmp")) throw new Exception("Git status not displayed");
            var nested = Path.Combine(root, "nested");
            Directory.CreateDirectory(nested);
            vm.SelectedProject = new ProjectEntry { FullPath = Path.Combine(nested, "nested.ymmp") };
            await Program.RunNamedCaseAsync("Pull diagnostics identify the repository and explain the disabled write command", async () =>
            {
                await Invoke("InspectGitPullAsync");
                if (!vm.GitStatus.Contains("ローカルの追跡情報を確認しました") ||
                    !vm.GitStatus.Contains(root, StringComparison.OrdinalIgnoreCase) ||
                    !vm.GitStatus.EndsWith(vm.PullUnavailableReason, StringComparison.Ordinal) ||
                    !vm.PullUnavailableReason.Contains("ブランチ・index・作業ファイル") || vm.PullGitCommand.CanExecute(null))
                    throw new InvalidOperationException("nested project Pull assessment did not explain its selected repository and write gate");

                vm.SelectedProject = new ProjectEntry { FullPath = Path.Combine(root, "missing", "project.ymmp") };
                await Invoke("InspectGitPullAsync");
                if (!vm.GitStatus.Contains("Git ルートを確認できません") ||
                    !vm.GitStatus.EndsWith(vm.PullUnavailableReason, StringComparison.Ordinal))
                    throw new InvalidOperationException("diagnostics hid the write gate when repository discovery failed");
            });
            vm.SelectedProject = new ProjectEntry { FullPath = project };
            await Invoke("FindAssetCandidatesAsync");
            if (!vm.AssetStatus.Contains("素材とは限りません") || !vm.AssetStatus.Contains("sample.png") || !vm.AssetStatus.Contains("note.txt")) throw new Exception("candidate labels missing");
            vm.AssetPath = "sample.png\nsample2.png\nsample.png";
            await Invoke("InspectAssetAsync");
            if (!vm.AssetStatus.Contains("SHA-256:") || !vm.AssetStatus.Contains("9 bytes") || !vm.AssetStatus.Contains("sample2.png: 6 bytes")) throw new Exception("asset inspection missing");
            vm.AssetPath = "sample.png\nmissing.png";
            await Invoke("InspectAssetAsync");
            if (!vm.AssetStatus.Contains("素材が見つかりません")) throw new Exception("missing asset not reported");
            Func<CancellationToken, Task> pending = token => Task.Delay(Timeout.Infinite, token);
            var cancelled = Invoke("ExecuteToolAsync", "Asset synthetic", pending);
            if (!vm.CancelToolCommand.CanExecute(null)) throw new Exception("cancel command unavailable");
            vm.CancelToolCommand.Execute(null);
            await cancelled;
            if (!vm.AssetStatus.Contains("中止しました") || vm.CancelToolCommand.CanExecute(null)) throw new Exception("cancellation status missing");
            await Program.RunNamedCaseAsync("List publication cancellation is shown in the recovery panel", async () =>
            {
                var operation = (string)typeof(ProjectListViewModel).GetField("InspectListPublicationOperation",
                    BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
                var priorAsset = vm.AssetStatus;
                var priorGit = vm.GitStatus;
                var interrupted = Invoke("ExecuteToolAsync", operation, pending);
                vm.CancelToolCommand.Execute(null);
                await interrupted;
                if (!vm.ProjectListPublicationStatus.Contains("中止しました") ||
                    vm.AssetStatus != priorAsset || vm.GitStatus != priorGit || vm.CancelToolCommand.CanExecute(null))
                    throw new InvalidOperationException("list publication cancellation reached the wrong panel");
            });
            vm.SelectedProject = null;
            if (vm.AssetStatus != string.Empty || vm.GitStatus != string.Empty) throw new Exception("stale status after selection");
            if (vm.OpenSelectedProjectRelinkCommand.CanExecute(null)) throw new Exception("relink enabled without selection");
            await Program.RunNamedCaseAsync("WPF conflict review requires an explicit decision for both rows", () =>
            {
                var first = new ProjectListConflict("first", "DisplayName", "local one", "disk one");
                var second = new ProjectListConflict("second", "Memo", "local two", "disk two");
                var dialog = new ProjectListConflictReview([first, second], owner: null);
                try
                {
                    if (dialog.AllDecisionsMade || dialog.Decisions.Count != 0) throw new Exception("A conflict had an implicit choice.");
                    dialog.ChooseCurrent(false);
                    if (dialog.AllDecisionsMade || dialog.Decisions[first]) throw new Exception("Disk-side choice was not retained.");
                    dialog.ChooseCurrent(true);
                    if (!dialog.AllDecisionsMade || !dialog.Decisions[second]) throw new Exception("Explicit choices were not completed.");
                }
                finally { dialog.Close(); }
                return Task.CompletedTask;
            });
            await Program.RunNamedCaseAsync("Undoing pending changes fences the abandoned WPF draft", async () =>
            {
                var listPath = Path.Combine(root, "reverted", "projects.json");
                var repository = new JsonProjectRepository(logger, listPath);
                var recoveryVm = new ProjectListViewModel(logger, repository);
                await recoveryVm.InitializeAsync();
                var pendingStore = await repository.LoadAsync();
                pendingStore.Projects.Add(new ProjectEntry { FullPath = project, ProjectId = Guid.NewGuid() });
                var draftSave = typeof(ProjectListViewModel).GetMethod("SavePendingDraftAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var drafts = new ProjectListDrafts(repository.RecoveryDirectory);

                await (Task)draftSave.Invoke(recoveryVm, [pendingStore])!;
                var predecessor = Directory.EnumerateFiles(drafts.DirectoryPath, "*.draft.json").Single();
                if ((await drafts.ReadForReviewAsync(predecessor)).ExportOnly)
                    throw new InvalidOperationException("The first pending change was not recoverable.");
                await (Task)draftSave.Invoke(recoveryVm, [await repository.LoadAsync()])!;
                var successor = Directory.EnumerateFiles(drafts.DirectoryPath, "*.draft.json").Single(path => path != predecessor);
                var newest = await drafts.ReadForReviewAsync(successor);
                if (!(await drafts.ReadForReviewAsync(predecessor)).ExportOnly || newest.ExportOnly ||
                    newest.Draft.Local.Projects.Count != 0 || File.Exists(listPath))
                    throw new InvalidOperationException("The abandoned pending change remained replayable or modified the active list.");
            });
            await Program.RunNamedCaseAsync("WPF preserves an unreadable interrupted list record", async () =>
            {
                var isolatedRoot = Path.Combine(root, "interrupted-list");
                Directory.CreateDirectory(isolatedRoot);
                var listPath = Path.Combine(isolatedRoot, "projects.json");
                var repository = new JsonProjectRepository(logger, listPath);
                var marker = repository.InterruptedPublicationPath;
                await File.WriteAllTextAsync(marker, "invalid journal fixture");
                var interrupted = new ProjectListViewModel(logger, repository);
                await interrupted.InitializeAsync();
                if (interrupted.Projects.Count != 0 || !interrupted.RecoverProjectListPublicationCommand.CanExecute(null) ||
                    !interrupted.ProjectListPublicationStatus.Contains("中断された一覧保存"))
                    throw new InvalidOperationException("WPF did not surface the pending record after load was refused.");

                await (Task)typeof(ProjectListViewModel).GetMethod("RecoverProjectListPublicationAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(interrupted, null)!;
                if (!interrupted.ProjectListPublicationStatus.Contains(marker, StringComparison.Ordinal) ||
                    await File.ReadAllTextAsync(marker) != "invalid journal fixture" || File.Exists(listPath))
                    throw new InvalidOperationException("WPF hid or modified evidence it could not interpret.");
            });
            await Program.RunNamedCaseAsync("List initialization can retry an earlier writer lock without reopening the UI", async () =>
            {
                var dataRoot = Path.Combine(root, "busy-list");
                Directory.CreateDirectory(dataRoot);
                var listPath = Path.Combine(dataRoot, "projects.json");
                var repository = new JsonProjectRepository(logger, listPath);
                var blockedVm = new ProjectListViewModel(logger, repository);
                var retry = typeof(ProjectListViewModel).GetMethod("RecoverProjectListPublicationAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;

                using (new FileStream(listPath + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    await blockedVm.InitializeAsync();
                    if (!blockedVm.ProjectListPublicationStatus.Contains("使用中") || blockedVm.Projects.Count != 0)
                        throw new InvalidOperationException("The UI hid its blocked load.");
                    await (Task)retry.Invoke(blockedVm, null)!;
                    if (!blockedVm.ProjectListPublicationStatus.Contains("使用中") || File.Exists(listPath))
                        throw new InvalidOperationException("The UI did not preserve the busy list.");
                }

                var saved = await repository.LoadAsync();
                saved.Projects.Add(new ProjectEntry { FullPath = project, ProjectId = Guid.NewGuid() });
                await repository.SaveAsync(saved);
                await (Task)retry.Invoke(blockedVm, null)!;
                if (blockedVm.Projects.Single().FullPath != project ||
                    !blockedVm.ProjectListPublicationStatus.Contains("読み込みました"))
                    throw new InvalidOperationException("The UI could not load the completed save on retry.");
            });
            Console.WriteLine("Offline Git and asset ViewModel checks passed.");
            return true;
        });
    }
}
