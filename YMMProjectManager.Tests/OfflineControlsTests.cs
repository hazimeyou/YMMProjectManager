using System.Diagnostics;
using System.IO;
using System.Reflection;
using YMMProjectManager.Application;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Git;
using YMMProjectManager.Presentation.ViewModels;
using YMMProjectManager.Presentation.Views;

//CSJ,class=OfflineControlsTests,オフライン制御テスト
internal static class OfflineControlsTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    public static async Task RunAsync(string workRoot)
    {
        await Program.WpfTestHost.RunAsync(async () =>
        {
            //CSJ,variable=root,ルート
            var root = Path.Combine(workRoot, "offline-controls"); Directory.CreateDirectory(root);
            //CSJ,variable=project,プロジェクト
            var project = Path.Combine(root, "project.ymmp");
            await File.WriteAllTextAsync(project, "{\"Items\":[{\"FilePath\":\"sample.png\"}],\"Other\":{\"FilePath\":\"note.txt\"}}");
            await File.WriteAllTextAsync(Path.Combine(root, "sample.png"), "synthetic");
            await File.WriteAllTextAsync(Path.Combine(root, "sample2.png"), "second");
            var git = new GitRepository(root);
            if (!(await git.InitializeAsync()).Success) throw new Exception("git init failed");
            //CSJ,variable=logger,ロガー
            var logger = new FileLogger(Path.Combine(root, "test.log"));
            var vm = new ProjectListViewModel(logger, new JsonProjectRepository(logger, Path.Combine(root, "project-list.json")));
            await vm.InitializeAsync();
            vm.SelectedProject = new ProjectEntry { FullPath = project };
            if (!vm.OpenSelectedProjectRelinkCommand.CanExecute(null)) throw new Exception("selected project relink unavailable");
            if (!vm.FetchGitCommand.CanExecute(null) || !vm.PullGitCommand.CanExecute(null) ||
                !vm.InspectGitPullCommand.CanExecute(null) || !vm.PushGitCommand.CanExecute(null) || !vm.GitCloneCommand.CanExecute(null))
                throw new Exception("trusted Git controls are unavailable for the selected project");
            if (!vm.RepairProjectListIdentitiesCommand.CanExecute(null))
                throw new Exception("physical identity review is unavailable for a readable project list");
            if (!vm.ExtractBundleCommand.CanExecute(null)) throw new Exception("managed extraction command unavailable");
            //CSJ,method=Invoke,呼び出す
            async Task Invoke(string name, params object[] args) => await (Task)typeof(ProjectListViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, args)!;
            await Invoke("ShowGitStatusAsync", false);
            await Program.RunNamedCaseAsync("R21 WPF lists Git changes as explicit selectable paths", () =>
            {
                if (!vm.GitStatus.Contains("Git:") || vm.GitChanges.SingleOrDefault(change => change.Path == "project.ymmp") is not { } projectChange ||
                    !projectChange.Display.Contains("project.ymmp") || !vm.CommitGitCommand.CanExecute(null) ||
                    !vm.ReviewProjectCleanupCommand.CanExecute(null) || !vm.CollectAssetToPoolCommand.CanExecute(null) ||
                    !vm.InspectGitHubAccessCommand.CanExecute(null))
                    throw new InvalidOperationException("The Git changes list or R21 commands are missing.");
                vm.SelectedGitChange = projectChange;
                if (vm.GitPath != "project.ymmp") throw new InvalidOperationException("The selected change did not set the explicit Git path.");
                return Task.CompletedTask;
            });
            await Program.RunNamedCaseAsync("R24 GitHub access approval and origin display use literal validated configuration", async () =>
            {
                const string github = "https://github.com/example/project.git";
                //CSJ,variable=replacement,置換
                const string replacement = "https://rewritten.invalid/example/project.git";
                //CSJ,variable=credentialUrl,資格情報URL
                const string credentialUrl = "https://fixture-user:fixture-secret@github.com/example/project.git";
                var unapprovedProbes = 0;
                Task<GitResult> UnexpectedProbe(GitRepository _, GitFetchTarget __, GitHubAuthentication ___, CancellationToken token)
                {
                    unapprovedProbes++;
                    return Task.FromResult(new GitResult(1, "", "", GitFailure.UnsafeOperation));
                }
                await RunGitFixtureAsync(root, "config", "remote.origin.url", github);
                await RunGitFixtureAsync(root, "config", "url." + replacement + ".insteadOf", github);
                await Invoke("ShowGitDetailsAsync", true);
                if (!vm.GitStatus.Contains(github) || vm.GitStatus.Contains(replacement))
                    throw new InvalidOperationException("Displayed origin was not the validated literal setting.");

                //CSJ,variable=cached,キャッシュ済み
                foreach (var cached in new[] { false, true })
                {
                    vm.UseCachedGitHubAuthentication = cached;
                    var approvals = 0;
                    //CSJ,variable=approvedDetails,承認済み詳細
                    var approvedDetails = "";
                    await vm.InspectGitHubAccessAsync((_, details) =>
                    {
                        approvals++;
                        approvedDetails = details;
                        return false;
                    }, UnexpectedProbe);
                    if (approvals != 1 || !approvedDetails.Contains(github) || approvedDetails.Contains(replacement) ||
                        (approvedDetails.Contains("Git Credential Manager") != cached) || !vm.GitStatus.Contains("取り消しました"))
                        throw new InvalidOperationException("Approval details, credential choice or cancellation was incorrect.");
                }

                vm.UseCachedGitHubAuthentication = false;
                var selected = vm.SelectedProject!;
                var approvalsAfterChange = 0;
                await vm.InspectGitHubAccessAsync((_, _) =>
                {
                    approvalsAfterChange++;
                    selected.FullPath = Path.Combine(root, "different.ymmp");
                    return true;
                }, UnexpectedProbe);
                selected.FullPath = project;
                if (approvalsAfterChange != 1 || !vm.GitStatus.Contains("選択中のプロジェクトが変わりました"))
                    throw new InvalidOperationException("Approval from a different project path was reused.");

                await RunGitFixtureAsync(root, "config", "remote.origin.url", credentialUrl);
                await Invoke("ShowGitDetailsAsync", true);
                var originStatus = vm.GitStatus;
                var unsafeApproval = false;
                await vm.InspectGitHubAccessAsync((_, _) => { unsafeApproval = true; return false; }, UnexpectedProbe);
                using var log = new StreamReader(new FileStream(Path.Combine(root, "test.log"),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                var logged = await log.ReadToEndAsync();
                if (unsafeApproval || unapprovedProbes != 0 || originStatus.Contains("fixture-secret") ||
                    vm.GitStatus.Contains("fixture-secret") || logged.Contains("fixture-secret"))
                    throw new InvalidOperationException("Configured URL credentials reached status or log.");

                await RunGitFixtureAsync(root, "config", "--remove-section", "remote.origin");
                await RunGitFixtureAsync(root, "config", "--remove-section", "url." + replacement);
            });
            await Program.RunNamedCaseAsync("R24 GitHub access check cancels and rejects a concurrent request", async () =>
            {
                const string github = "https://github.com/example/project.git";
                await RunGitFixtureAsync(root, "config", "remote.origin.url", github);
                vm.UseCachedGitHubAuthentication = true;
                var approvals = 0;
                bool Approve(string _, string __) { approvals++; return true; }
                var started = new TaskCompletionSource<(string Endpoint, GitHubAuthentication Authentication)>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                //CSJ,variable=blocked,停止
                var blocked = new TaskCompletionSource<GitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                //CSJ,variable=operation,操作
                var operation = vm.InspectGitHubAccessAsync(Approve, (_, target, authentication, token) =>
                {
                    started.TrySetResult((target.Endpoint, authentication));
                    return blocked.Task.WaitAsync(token);
                });
                try
                {
                    //CSJ,variable=request,要求
                    var request = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    if (request != (github, GitHubAuthentication.CachedWindows) || !vm.IsBusy || !vm.CancelToolCommand.CanExecute(null))
                        throw new InvalidOperationException("The approved request is not pending with the selected credential mode.");
                    await vm.InspectGitHubAccessAsync(Approve, (_, _, _, _) => Task.FromResult(new GitResult(0, "", "")));
                    if (approvals != 1) throw new InvalidOperationException("Concurrent access check reached approval.");
                    vm.CancelToolCommand.Execute(null);
                    await operation.WaitAsync(TimeSpan.FromSeconds(10));
                    if (vm.IsBusy || !vm.UseCachedGitHubAuthentication || !vm.GitStatus.Contains("中止しました"))
                        throw new InvalidOperationException("Cancellation did not preserve the cached opt-in and restore the controls.");
                }
                finally
                {
                    blocked.TrySetCanceled();
                    await operation;
                    vm.UseCachedGitHubAuthentication = false;
                    await RunGitFixtureAsync(root, "config", "--remove-section", "remote.origin");
                }
            });
            await Program.RunNamedCaseAsync("R24 auth diagnostics distinguish retry modes without child output", () =>
            {
                var result = new GitResult(1, "fixture-output", "fixture-error", GitFailure.Authentication);
                var anonymous = ProjectListViewModel.DescribeRemoteFailure(result, isGitHub: true);
                //CSJ,variable=cached,キャッシュ済み
                var cached = ProjectListViewModel.DescribeRemoteFailure(result, isGitHub: true, GitHubAuthentication.CachedWindows);
                var denied = ProjectListViewModel.DescribeRemoteFailure(result with { Failure = GitFailure.Permission },
                    isGitHub: true, GitHubAuthentication.CachedWindows);
                if (!anonymous.Contains("承認し直して") || !cached.Contains("有効期限") || !cached.Contains("再ログイン") ||
                    !denied.Contains("権限") || denied.Contains("有効期限") ||
                    (anonymous + cached + denied).Contains("fixture-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Authentication modes or child-output redaction were lost.");
                return Task.CompletedTask;
            });
            await Program.RunNamedCaseAsync("R24 browser login primes neither Git nor credential opt-in", async () =>
            {
                //CSJ,variable=savedProject,保存済みプロジェクト
                var savedProject = vm.SelectedProject;
                //CSJ,variable=initialStatus,初期状態
                var initialStatus = await RunGitFixtureAsync(root, "status", "--porcelain");
                vm.SelectedProject = null;
                vm.GitRemote = "https://example.invalid/not-a-login-target.git";
                var approvals = new List<bool>();
                var launches = new List<bool>();
                bool Approve(bool reauthenticate) { approvals.Add(reauthenticate); return true; }
                Task<GitResult> SignIn(bool reauthenticate, CancellationToken token)
                {
                    launches.Add(reauthenticate);
                    return Task.FromResult(new GitResult(0, "fixture-browser-stdout", ""));
                }

                try
                {
                    if (!vm.GitHubBrowserLoginCommand.CanExecute(null) || !vm.GitHubBrowserReloginCommand.CanExecute(null))
                        throw new InvalidOperationException("Browser login is inaccessible before clone.");
                    await vm.RunGitHubBrowserLoginAsync(false, Approve, _ => Task.FromResult(false), SignIn);
                    if (approvals.Count != 0 || launches.Count != 0 || !vm.GitStatus.Contains("検証済み"))
                        throw new InvalidOperationException("An unsupported installation reached approval or launch.");

                    await vm.RunGitHubBrowserLoginAsync(false, mode => { approvals.Add(mode); return false; },
                        _ => Task.FromResult(true), SignIn);
                    if (approvals.Count != 1 || launches.Count != 0 || !vm.GitStatus.Contains("取り消しました"))
                        throw new InvalidOperationException("Declined approval reached launch.");

                    foreach (var reauthenticate in new[] { false, true })
                    {
                        vm.UseCachedGitHubAuthentication = reauthenticate;
                        await vm.RunGitHubBrowserLoginAsync(reauthenticate, Approve, _ => Task.FromResult(true), SignIn);
                        if (vm.UseCachedGitHubAuthentication != reauthenticate || vm.IsBusy ||
                            !vm.GitStatus.Contains("が完了しました") || vm.GitStatus.Contains("fixture-browser-") ||
                            vm.SelectedProject is not null || vm.GitRemote != "https://example.invalid/not-a-login-target.git")
                            throw new InvalidOperationException("Browser completion altered another Git decision or exposed helper output.");
                    }
                    if (!approvals.SequenceEqual(new[] { false, false, true }) ||
                        !launches.SequenceEqual(new[] { false, true }) ||
                        await RunGitFixtureAsync(root, "status", "--porcelain") != initialStatus)
                        throw new InvalidOperationException("Initial/relogin modes or the pre-existing local working changes were lost.");
                }
                finally
                {
                    vm.SelectedProject = savedProject;
                    vm.GitRemote = "";
                    vm.UseCachedGitHubAuthentication = false;
                }
            });
            await Program.RunNamedCaseAsync("R24 unexpected browser authentication failures stay out of status and logs", async () =>
            {
                //CSJ,variable=marker,マーカー
                const string marker = "fixture-browser-failure-secret";
                await vm.RunGitHubBrowserLoginAsync(false, _ => true, _ => Task.FromResult(true),
                    (_, _) => Task.FromException<GitResult>(new IOException(marker)));
                using var log = new StreamReader(new FileStream(Path.Combine(root, "test.log"),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                var logged = await log.ReadToEndAsync();
                if (vm.IsBusy || !vm.GitStatus.Contains("結果を確認できません") || vm.GitStatus.Contains(marker) || logged.Contains(marker))
                    throw new InvalidOperationException("An unexpected helper failure leaked details or was not shown to the user.");
            });
            await Program.RunNamedCaseAsync("R24 browser cancel distinguishes prelaunch from a possibly published credential", async () =>
            {
                var earlyApprovals = 0;
                var earlyLaunches = 0;
                await vm.RunGitHubBrowserLoginAsync(false, _ => { earlyApprovals++; return true; }, token =>
                {
                    vm.CancelGitHubBrowserLoginCommand.Execute(null);
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(true);
                }, (_, _) => { earlyLaunches++; return Task.FromResult(new GitResult(0, "", "")); });
                if (earlyApprovals != 0 || earlyLaunches != 0 || !vm.GitStatus.Contains("中止しました"))
                    throw new InvalidOperationException("Prelaunch cancellation was not reported.");

                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                //CSJ,variable=operation,操作
                var operation = vm.RunGitHubBrowserLoginAsync(true, _ => true, _ => Task.FromResult(true),
                    async (_, token) =>
                    {
                        //CSJ,variable=cancelled,キャンセル済み
                        using var cancelled = token.Register(() => completed.TrySetResult());
                        entered.TrySetResult();
                        await completed.Task;
                        return new GitResult(1, "fixture-helper-stdout", "fixture-helper-error", GitFailure.OutcomeUnknown);
                    });
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    if (!vm.CancelGitHubBrowserLoginCommand.CanExecute(null) || vm.GitHubBrowserLoginCommand.CanExecute(null) ||
                        vm.GitHubBrowserReloginCommand.CanExecute(null) || vm.FetchGitCommand.CanExecute(null))
                        throw new InvalidOperationException("An in-flight login did not fence the Git commands.");
                    vm.CancelGitHubBrowserLoginCommand.Execute(null);
                    await operation.WaitAsync(TimeSpan.FromSeconds(10));
                    if (!vm.GitStatus.Contains("更新された可能性") || vm.GitStatus.Contains("中止しました") ||
                        vm.GitStatus.Contains("fixture-helper-") || vm.IsBusy || vm.CancelGitHubBrowserLoginCommand.CanExecute(null))
                        throw new InvalidOperationException("Possibly published credentials were reported as definitely unchanged.");
                }
                finally
                {
                    completed.TrySetResult();
                    await operation;
                }
            });
            //CSJ,variable=nested,入れ子
            var nested = Path.Combine(root, "nested");
            Directory.CreateDirectory(nested);
            vm.SelectedProject = new ProjectEntry { FullPath = Path.Combine(nested, "nested.ymmp") };
            if (vm.GitChanges.Count != 0 || vm.GitPath != "")
                throw new InvalidOperationException("A different project inherited the selected Git path.");
            await Program.RunNamedCaseAsync("Pull diagnostics identify the repository and explain command availability", async () =>
            {
                await Invoke("InspectGitPullAsync");
                if (!vm.GitStatus.Contains("ローカルの追跡情報を確認しました") ||
                    !vm.GitStatus.Contains(root, StringComparison.OrdinalIgnoreCase) ||
                    !vm.GitStatus.EndsWith(vm.PullHelp, StringComparison.Ordinal) ||
                    !vm.PullGitCommand.CanExecute(null))
                    throw new InvalidOperationException("nested project Pull assessment did not explain its repository and approval path");

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
            //CSJ,variable=pending,保留
            Func<CancellationToken, Task> pending = token => Task.Delay(Timeout.Infinite, token);
            //CSJ,variable=cancelled,キャンセル済み
            var cancelled = Invoke("ExecuteToolAsync", "Asset synthetic", pending);
            if (!vm.CancelToolCommand.CanExecute(null)) throw new Exception("cancel command unavailable");
            vm.CancelToolCommand.Execute(null);
            await cancelled;
            if (!vm.AssetStatus.Contains("中止しました") || vm.CancelToolCommand.CanExecute(null)) throw new Exception("cancellation status missing");
            await Program.RunNamedCaseAsync("List publication cancellation is shown in the recovery panel", async () =>
            {
                //CSJ,variable=operation,操作
                var operation = (string)typeof(ProjectListViewModel).GetField("InspectListPublicationOperation",
                    BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
                var priorAsset = vm.AssetStatus;
                var priorGit = vm.GitStatus;
                //CSJ,variable=interrupted,中断済み
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
            if (vm.PullGitCommand.CanExecute(null)) throw new Exception("Pull enabled without selection");
            await Program.RunNamedCaseAsync("WPF conflict review requires an explicit decision for both rows", () =>
            {
                var first = new ProjectListConflict("first", "DisplayName", "local one", "disk one");
                var second = new ProjectListConflict("second", "Memo", "local two", "disk two");
                //CSJ,variable=dialog,ダイアログ
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
                //CSJ,variable=listPath,一覧パス
                var listPath = Path.Combine(root, "reverted", "projects.json");
                //CSJ,variable=repository,リポジトリ
                var repository = new JsonProjectRepository(logger, listPath);
                //CSJ,variable=recoveryVm,復旧ビューモデル
                var recoveryVm = new ProjectListViewModel(logger, repository);
                await recoveryVm.InitializeAsync();
                //CSJ,variable=pendingStore,保留保管先
                var pendingStore = await repository.LoadAsync();
                pendingStore.Projects.Add(new ProjectEntry { FullPath = project, ProjectId = Guid.NewGuid() });
                //CSJ,variable=draftSave,下書き保存
                var draftSave = typeof(ProjectListViewModel).GetMethod("SavePendingDraftAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                //CSJ,variable=drafts,下書き一覧
                var drafts = new ProjectListDrafts(repository.RecoveryDirectory);

                await (Task)draftSave.Invoke(recoveryVm, [pendingStore])!;
                var predecessor = Directory.EnumerateFiles(drafts.DirectoryPath, "*.draft.json").Single();
                if ((await drafts.ReadForReviewAsync(predecessor)).ExportOnly)
                    throw new InvalidOperationException("The first pending change was not recoverable.");
                await (Task)draftSave.Invoke(recoveryVm, [await repository.LoadAsync()])!;
                var successor = Directory.EnumerateFiles(drafts.DirectoryPath, "*.draft.json").Single(path => path != predecessor);
                //CSJ,variable=newest,最新
                var newest = await drafts.ReadForReviewAsync(successor);
                if (!(await drafts.ReadForReviewAsync(predecessor)).ExportOnly || newest.ExportOnly ||
                    newest.Draft.Local.Projects.Count != 0 || File.Exists(listPath))
                    throw new InvalidOperationException("The abandoned pending change remained replayable or modified the active list.");
            });
            await Program.RunNamedCaseAsync("WPF preserves an unreadable interrupted list record", async () =>
            {
                //CSJ,variable=isolatedRoot,隔離ルート
                var isolatedRoot = Path.Combine(root, "interrupted-list");
                Directory.CreateDirectory(isolatedRoot);
                //CSJ,variable=listPath,一覧パス
                var listPath = Path.Combine(isolatedRoot, "projects.json");
                //CSJ,variable=repository,リポジトリ
                var repository = new JsonProjectRepository(logger, listPath);
                //CSJ,variable=marker,マーカー
                var marker = repository.InterruptedPublicationPath;
                await File.WriteAllTextAsync(marker, "invalid journal fixture");
                //CSJ,variable=interrupted,中断済み
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
                //CSJ,variable=dataRoot,データルート
                var dataRoot = Path.Combine(root, "busy-list");
                Directory.CreateDirectory(dataRoot);
                //CSJ,variable=listPath,一覧パス
                var listPath = Path.Combine(dataRoot, "projects.json");
                //CSJ,variable=repository,リポジトリ
                var repository = new JsonProjectRepository(logger, listPath);
                //CSJ,variable=blockedVm,停止ビューモデル
                var blockedVm = new ProjectListViewModel(logger, repository);
                //CSJ,variable=retry,再試行
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

                //CSJ,variable=saved,保存済み
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
        //CSJ,variable=fixture,テスト用データ
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
            //CSJ,variable=sourceProject,元プロジェクト
            var sourceProject = Path.Combine(source, "project.ymmp");
            //CSJ,variable=oldJson,旧JSON
            const string oldJson = """{"Title":"Before"}""";
            //CSJ,variable=newJson,新規JSON
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
            //CSJ,variable=afterTree,変更後ツリー
            var afterTree = await RunGitFixtureAsync(source, "rev-parse", "HEAD^{tree}");
            await RunGitFixtureAsync(source, "push", bare, "main:refs/heads/main");

            //CSJ,variable=project,プロジェクト
            var project = Path.Combine(checkout, "project.ymmp");
            vm.SelectedProject = new ProjectEntry { FullPath = project };
            //CSJ,variable=trusted,信頼済み
            var trusted = false;
            //CSJ,variable=approved,承認済み
            var approved = false;
            //CSJ,method=InspectTransport,通信を点検する
            bool InspectTransport(string repository, string remote)
            {
                trusted = string.Equals(repository, checkout, StringComparison.OrdinalIgnoreCase) &&
                    remote.Contains("origin / refs/heads/main", StringComparison.Ordinal);
                return trusted;
            }
            //CSJ,method=InspectTarget,対象を点検する
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

            await Program.RunNamedCaseAsync("WPF keeps interrupted Pull evidence and refuses a second approval", async () =>
            {
                //CSJ,variable=marker,マーカー
                var marker = GitPullOperationJournal.GetPath(checkout);
                using (GitPullOperationJournal.Create(checkout,
                    new GitPullOperationJournal.ApprovalEvidence("refs/heads/main", before, "origin", "refs/heads/main", after)))
                {
                    // A disposed marker with no verified outcome represents an interrupted Pull.
                }
                //CSJ,variable=record,記録
                var record = await File.ReadAllBytesAsync(marker);
                //CSJ,variable=indexPath,インデックスパス
                var indexPath = Path.Combine(checkout, ".git", "index");
                //CSJ,variable=index,インデックス
                var index = await File.ReadAllBytesAsync(indexPath);
                //CSJ,variable=attempt,試行
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    await vm.RunConfirmedGitPullAsync(
                        (_, _) => throw new InvalidOperationException("Pull prompted for transport with unresolved evidence."),
                        _ => throw new InvalidOperationException("Pull prompted for mutation with unresolved evidence."));
                    if (!vm.GitStatus.Contains("確定できません") || !vm.GitStatus.Contains(marker) ||
                        !vm.GitStatus.Contains("回復記録が残っている間は Pull を開始できません"))
                        throw new InvalidOperationException("WPF did not report the unresolved operation and its recovery record.");
                }

                //CSJ,variable=finalRecord,最終記録
                var finalRecord = await File.ReadAllBytesAsync(marker);
                //CSJ,variable=finalIndex,最終インデックス
                var finalIndex = await File.ReadAllBytesAsync(indexPath);
                if (!record.SequenceEqual(finalRecord) || !index.SequenceEqual(finalIndex) ||
                    await File.ReadAllTextAsync(project) != newJson || await RunGitFixtureAsync(checkout, "rev-parse", "HEAD") != after)
                    throw new InvalidOperationException("Retry changed the worktree, ref, index, or recovery record.");
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", oldGlobal);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", oldSystem);
        }
    }

    //CSJ,method=RunGitFixtureAsync,Gitテスト用データを実行する（非同期）
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
        //CSJ,variable=argument,引数
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        //CSJ,variable=process,プロセス
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start fixture Git.");
        var output = process.StandardOutput.ReadToEndAsync();
        //CSJ,variable=error,エラー
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Git fixture failed with exit {process.ExitCode}: {await error}");
        await error;
        return (await output).TrimEnd();
    }

    //CSJ,method=TestPullPresentationAsync,プル表示をテストする（非同期）
    private static async Task TestPullPresentationAsync(ProjectListViewModel vm, string root)
    {
        //CSJ,variable=remote,リモート
        var remote = new object();
        var target = new object();
        var detections = 0;
        var executions = 0;
        var unexpectedCallback = false;
        //CSJ,method=InspectRemote,リモートを点検する
        Task<object?> InspectRemote(ProjectEntry project, CancellationToken token) => Task.FromResult<object?>(remote);
        //CSJ,method=InspectTarget,対象を点検する
        Task<object?> InspectTarget(object approved, CancellationToken token)
        {
            if (!ReferenceEquals(approved, remote)) unexpectedCallback = true;
            detections++;
            return Task.FromResult<object?>(target);
        }
        //CSJ,method=Execute,実行する
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
            //CSJ,variable=project,プロジェクト
            var project = vm.SelectedProject!;
            //CSJ,variable=originalPath,元のパス
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

            //CSJ,variable=pending,保留
            var pending = vm.RunGitPullWorkflowAsync(InspectRemote, _ => true,
                async (_, token) =>
                {
                    await Task.Delay(Timeout.Infinite, token);
                    return target;
                },
                _ => { unexpectedCallback = true; return false; }, Execute);
            if (vm.PullGitCommand.CanExecute(null) || !vm.CancelToolCommand.CanExecute(null) ||
                !vm.GitStatus.Contains("更新先コミットを確認中"))
                throw new InvalidOperationException("Pull lookup did not expose cancellable progress.");
            //CSJ,variable=dispatcher,ディスパッチャー
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var dispatched = await dispatcher.InvokeAsync(() => vm.CancelToolCommand.CanExecute(null));
            if (!dispatched || pending.IsCompleted)
                throw new InvalidOperationException("The WPF dispatcher could not process a callback during the pending Pull.");
            vm.CancelToolCommand.Execute(null);
            await pending;
            if (unexpectedCallback || executions != 1 || !vm.GitStatus.Contains("中止しました") || vm.CancelToolCommand.CanExecute(null))
                throw new InvalidOperationException("Cancelled Pull wrote repository data or left the cancellation command active.");
        });

        await Program.RunNamedCaseAsync("Pull confirmation identifies the exact operation and explains external interference", () =>
        {
            //CSJ,variable=details,詳細
            var details = new ProjectListViewModel.PullApprovalDetails(root, "refs/heads/main", "origin",
                "refs/heads/upstream", "https://example.invalid/project", new string('a', 40), new string('b', 40), true);
            //CSJ,variable=message,メッセージ
            var message = ProjectListViewModel.DescribePullApproval(details);
            //CSJ,variable=field,フィールド
            foreach (var field in new[] { root, details.BranchRef, details.RemoteName, details.UpstreamRef,
                details.Endpoint, details.StartObjectId, details.TargetObjectId, "Fast-forward", "作業ファイルを更新", "完全には防止できません" })
                if (!message.Contains(field, StringComparison.Ordinal)) throw new InvalidOperationException("Missing approval detail.");
            if (!ProjectListViewModel.DescribePullApproval(details with { ChangesWorktree = false }).Contains("作業ファイルは更新しません"))
                throw new InvalidOperationException("No-op Pull was presented as a write.");
            //CSJ,variable=dialog,ダイアログ
            var dialog = ProjectListViewModel.CreatePullConfirmation(details);
            //CSJ,variable=panel,パネル
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
            //CSJ,variable=evidence,証跡
            var evidence = Path.Combine(root, ".git", "pull-recovery.json");
            var result = new GitResult(1, "", "untrusted process text", GitFailure.OutcomeUnknown) { RecoveryPath = evidence };
            //CSJ,variable=message,メッセージ
            var message = ProjectListViewModel.DescribePullFailure(root, result, "refs/heads/main");
            if (!message.Contains(root) || !message.Contains("refs/heads/main") || !message.Contains(evidence) ||
                !message.Contains("確定できません") || !message.Contains("確認が済むまで再実行") ||
                message.Contains("untrusted process text", StringComparison.Ordinal))
                throw new InvalidOperationException("The recovery message lost evidence, leaked stderr, or recommended a blind retry.");
            return Task.CompletedTask;
        });
    }
}
