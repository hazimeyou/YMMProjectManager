using System.Diagnostics;
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
            if (!vm.FetchGitCommand.CanExecute(null) || vm.PullGitCommand.CanExecute(null) != ProjectListViewModel.IsConfirmedPullEnabled ||
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
            await Program.RunNamedCaseAsync("Pull diagnostics identify the repository and explain command availability", async () =>
            {
                await Invoke("InspectGitPullAsync");
                if (!vm.GitStatus.Contains("ローカルの追跡情報を確認しました") ||
                    !vm.GitStatus.Contains(root, StringComparison.OrdinalIgnoreCase) ||
                    !vm.GitStatus.EndsWith(vm.PullHelp, StringComparison.Ordinal) ||
                    vm.PullGitCommand.CanExecute(null) != ProjectListViewModel.IsConfirmedPullEnabled)
                    throw new InvalidOperationException("nested project Pull assessment did not explain its selected repository and write gate");

                vm.SelectedProject = new ProjectEntry { FullPath = Path.Combine(root, "missing", "project.ymmp") };
                await Invoke("InspectGitPullAsync");
                if (!vm.GitStatus.Contains("Git ルートを確認できません") ||
                    !vm.GitStatus.EndsWith(vm.PullHelp, StringComparison.Ordinal))
                    throw new InvalidOperationException("diagnostics hid the write gate when repository discovery failed");
            });
            vm.SelectedProject = new ProjectEntry { FullPath = project };
            await TestPullPresentationAsync(vm, root);
            await Program.RunNamedCaseAsync("WPF Pull binds consent to an isolated remote and reports the verified checkout",
                () => TestProductPullAsync(vm, root));
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

    private static async Task TestProductPullAsync(ProjectListViewModel vm, string root)
    {
        var fixture = Directory.CreateDirectory(Path.Combine(root, "pull-ui")).FullName;
        var source = Path.Combine(fixture, "source");
        var bare = Path.Combine(fixture, "remote.git");
        var checkout = Path.Combine(fixture, "checkout");
        var oldGlobal = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        var oldSystem = Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", Path.Combine(fixture, "empty-config"));
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        try
        {
            await RunGitFixtureAsync(fixture, "init", "--initial-branch=main", source);
            await RunGitFixtureAsync(fixture, "init", "--bare", "--initial-branch=main", bare);
            await RunGitFixtureAsync(source, "config", "user.name", "WPF Test");
            await RunGitFixtureAsync(source, "config", "user.email", "wpf-test@example.invalid");
            var sourceProject = Path.Combine(source, "project.ymmp");
            const string oldJson = """{"Title":"Before"}""";
            const string newJson = """{"Title":"After"}""";
            await File.WriteAllTextAsync(sourceProject, oldJson);
            await RunGitFixtureAsync(source, "add", "--", "project.ymmp");
            await RunGitFixtureAsync(source, "commit", "-m", "before");
            await RunGitFixtureAsync(source, "push", bare, "main:refs/heads/main");
            await RunGitFixtureAsync(fixture, "clone", "--no-local", "--", bare, checkout);
            var before = await RunGitFixtureAsync(checkout, "rev-parse", "HEAD");
            await File.WriteAllTextAsync(sourceProject, newJson);
            await RunGitFixtureAsync(source, "add", "--", "project.ymmp");
            await RunGitFixtureAsync(source, "commit", "-m", "after");
            var after = await RunGitFixtureAsync(source, "rev-parse", "HEAD");
            var afterTree = await RunGitFixtureAsync(source, "rev-parse", "HEAD^{tree}");
            await RunGitFixtureAsync(source, "push", bare, "main:refs/heads/main");

            var project = Path.Combine(checkout, "project.ymmp");
            vm.SelectedProject = new ProjectEntry { FullPath = project };
            var trusted = false;
            var approved = false;
            bool InspectTransport(string repository, string remote)
            {
                trusted = string.Equals(repository, checkout, StringComparison.OrdinalIgnoreCase) &&
                    remote.Contains("origin / refs/heads/main", StringComparison.Ordinal);
                return trusted;
            }
            bool InspectTarget(ProjectListViewModel.PullApprovalDetails details)
            {
                approved = details.ChangesWorktree && details.StartObjectId == before && details.TargetObjectId == after &&
                    details.BranchRef == "refs/heads/main" && details.UpstreamRef == "refs/heads/main";
                return approved;
            }

            await vm.RunConfirmedGitPullAsync(InspectTransport, details => { InspectTarget(details); return false; });
            if (!trusted || !approved || !vm.GitStatus.Contains("取り消しました") ||
                await RunGitFixtureAsync(checkout, "rev-parse", "HEAD") != before ||
                await File.ReadAllTextAsync(project) != oldJson)
                throw new InvalidOperationException($"A declined Pull reached the checkout or showed a different offer. {vm.GitStatus}");

            trusted = approved = false;
            await vm.RunConfirmedGitPullAsync(InspectTransport, InspectTarget);
            if (!trusted || !approved || !vm.GitStatus.Contains("Pull が完了しました") || !vm.GitStatus.Contains(after) ||
                await RunGitFixtureAsync(checkout, "rev-parse", "HEAD") != after ||
                await RunGitFixtureAsync(checkout, "write-tree") != afterTree ||
                await RunGitFixtureAsync(checkout, "status", "--porcelain") != "" ||
                await File.ReadAllTextAsync(project) != newJson)
                throw new InvalidOperationException($"WPF did not report a verified ref, index and worktree. {vm.GitStatus}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", oldGlobal);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", oldSystem);
        }
    }

    private static async Task<string> RunGitFixtureAsync(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start fixture Git.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Git fixture failed with exit {process.ExitCode}: {await error}");
        await error;
        return (await output).TrimEnd();
    }

    private static async Task TestPullPresentationAsync(ProjectListViewModel vm, string root)
    {
        var remote = new object();
        var target = new object();
        var detections = 0;
        var executions = 0;
        var unexpectedCallback = false;
        Task<object?> InspectRemote(ProjectEntry project, CancellationToken token) => Task.FromResult<object?>(remote);
        Task<object?> InspectTarget(object approved, CancellationToken token)
        {
            if (!ReferenceEquals(approved, remote)) unexpectedCallback = true;
            detections++;
            return Task.FromResult<object?>(target);
        }
        Task<string> Execute(object approved, CancellationToken token)
        {
            if (!ReferenceEquals(approved, target) || !vm.GitStatus.Contains("実行中")) unexpectedCallback = true;
            executions++;
            return Task.FromResult("確定できません。対象を手動で確認してください。");
        }

        await Program.RunNamedCaseAsync("Pull presentation obtains trust consent before lookup and exact approval before execution", async () =>
        {
            await vm.RunGitPullWorkflowAsync(InspectRemote, _ => false, InspectTarget,
                _ => { unexpectedCallback = true; return false; }, Execute);
            if (unexpectedCallback || detections != 0 || executions != 0 || !vm.GitStatus.Contains("取り消しました"))
                throw new InvalidOperationException("Transport denial started a connection.");
            await vm.RunGitPullWorkflowAsync(InspectRemote, _ => true, InspectTarget, _ => false, Execute);
            if (detections != 1 || executions != 0 || !vm.GitStatus.Contains("作業ツリーは更新していません"))
                throw new InvalidOperationException("Pull denial started a write.");

            await vm.RunGitPullWorkflowAsync(InspectRemote, _ => true, InspectTarget, _ => true, Execute);
            if (unexpectedCallback || detections != 2 || executions != 1 || !vm.GitStatus.Contains("手動で確認") ||
                vm.CancelToolCommand.CanExecute(null))
                throw new InvalidOperationException("The ambiguous result was retried or not displayed.");
        });

        await Program.RunNamedCaseAsync("Pull presentation stops on selection path change and cancellation before write", async () =>
        {
            var project = vm.SelectedProject!;
            var originalPath = project.FullPath;
            try
            {
                await vm.RunGitPullWorkflowAsync(InspectRemote, _ => true, InspectTarget, _ =>
                {
                    project.FullPath = Path.Combine(root, "other.ymmp");
                    return true;
                }, Execute);
            }
            finally { project.FullPath = originalPath; }
            if (executions != 1 || !vm.GitStatus.Contains("プロジェクトが変更"))
                throw new InvalidOperationException("Stale approval was executed.");

            var pending = vm.RunGitPullWorkflowAsync(InspectRemote, _ => true,
                async (_, token) =>
                {
                    await Task.Delay(Timeout.Infinite, token);
                    return target;
                },
                _ => { unexpectedCallback = true; return false; }, Execute);
            if (!vm.CancelToolCommand.CanExecute(null) || !vm.GitStatus.Contains("更新先コミットを確認中"))
                throw new InvalidOperationException("Pull lookup did not expose cancellable progress.");
            vm.CancelToolCommand.Execute(null);
            await pending;
            if (unexpectedCallback || executions != 1 || !vm.GitStatus.Contains("中止しました") || vm.CancelToolCommand.CanExecute(null))
                throw new InvalidOperationException("Cancelled Pull wrote repository data or left the cancellation command active.");
        });

        await Program.RunNamedCaseAsync("Pull confirmation identifies the exact operation and explains external interference", () =>
        {
            var details = new ProjectListViewModel.PullApprovalDetails(root, "refs/heads/main", "origin",
                "refs/heads/upstream", "https://example.invalid/project", new string('a', 40), new string('b', 40), true);
            var message = ProjectListViewModel.DescribePullApproval(details);
            foreach (var field in new[] { root, details.BranchRef, details.RemoteName, details.UpstreamRef,
                details.Endpoint, details.StartObjectId, details.TargetObjectId, "Fast-forward", "作業ファイルを更新", "完全には防止できません" })
                if (!message.Contains(field, StringComparison.Ordinal)) throw new InvalidOperationException("Missing approval detail.");
            if (!ProjectListViewModel.DescribePullApproval(details with { ChangesWorktree = false }).Contains("作業ファイルは更新しません"))
                throw new InvalidOperationException("No-op Pull was presented as a write.");
            var dialog = ProjectListViewModel.CreatePullConfirmation(details);
            var panel = (System.Windows.Controls.DockPanel)dialog.Content;
            var buttons = ((System.Windows.Controls.StackPanel)panel.Children[0]).Children
                .OfType<System.Windows.Controls.Button>().ToArray();
            if (buttons.Length != 2 || !buttons[0].IsCancel || !buttons[0].IsDefault ||
                buttons[1].IsDefault || !Equals(buttons[1].Content, "Pull を実行"))
                throw new InvalidOperationException("Pull is the default dialog action or cancel is unavailable.");
            dialog.Close();
            return Task.CompletedTask;
        });
        await Program.RunNamedCaseAsync("A late cancellation does not hide the verified Pull outcome", async () =>
        {
            var backendCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var enteredExecution = false;
            var running = vm.RunGitPullWorkflowAsync(InspectRemote, _ => true, InspectTarget, _ => true,
                (approved, token) =>
                {
                    enteredExecution = ReferenceEquals(approved, target);
                    return backendCompletion.Task;
                });
            var cancellable = vm.CancelToolCommand.CanExecute(null);
            vm.CancelToolCommand.Execute(null);
            backendCompletion.SetResult("Pull が完了しました。検証済みです。");
            await running;
            if (!enteredExecution || !cancellable || !vm.GitStatus.Contains("検証済み") || vm.CancelToolCommand.CanExecute(null))
                throw new InvalidOperationException("The presentation discarded the controlled post-mutation verification.");
        });
        await Program.RunNamedCaseAsync("Pull recovery message identifies the repository and avoids claiming rollback", () =>
        {
            var evidence = Path.Combine(root, ".git", "pull-recovery.json");
            var result = new GitResult(1, "", "untrusted process text", GitFailure.OutcomeUnknown) { RecoveryPath = evidence };
            var message = ProjectListViewModel.DescribePullFailure(root, result, "refs/heads/main");
            if (!message.Contains(root) || !message.Contains("refs/heads/main") || !message.Contains(evidence) ||
                !message.Contains("確定できません") || !message.Contains("確認が済むまで再実行") ||
                message.Contains("untrusted process text", StringComparison.Ordinal))
                throw new InvalidOperationException("The recovery message lost evidence, leaked stderr, or recommended a blind retry.");
            return Task.CompletedTask;
        });
    }
}
