using System.Diagnostics;
using System.IO;
using YMMProjectManager.Infrastructure.Git;

//CSJ,class=GitRepositoryTests,Gitリポジトリテスト
internal static class GitRepositoryTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    public static async Task RunAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(workRoot, "git-core");
        Directory.CreateDirectory(root);
        var git = new GitRepository(root);
        //CSJ,method=Check,確認する
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        Check((await git.InitializeAsync()).Success, "git init");
        foreach (var (key, value) in new[] { ("user.name", "Synthetic Tester"), ("user.email", "synthetic@example.invalid") })
            await CommandAsync(root, "config", "--local", key, value);
        //CSJ,variable=marker,マーカー
        var marker = Path.Combine(root, "fsmonitor-ran");
        var monitor = Path.Combine(root, "monitor.sh");
        await File.WriteAllTextAsync(monitor, "#!/bin/sh\nprintf invoked > fsmonitor-ran\n");
        await CommandAsync(root, "config", "--local", "core.fsmonitor", monitor.Replace('\\', '/'));
        await git.StatusAsync();
        Check(!File.Exists(marker), "untrusted fsmonitor was executed");
        await File.WriteAllTextAsync(Path.Combine(root, "one.txt"), "original");
        await File.WriteAllTextAsync(Path.Combine(root, "-special.txt"), "literal path");
        var staged = await git.StageAsync(["one.txt", "-special.txt"]);
        Check(staged.Success, "stage paths: " + staged.StandardError);
        Check((await git.CommitAsync("first commit")).Success, "commit");
        var hooks = Path.Combine(root, "injected-hooks"); Directory.CreateDirectory(hooks);
        await File.WriteAllTextAsync(Path.Combine(hooks, "pre-commit"), "#!/bin/sh\nprintf invoked > injected-hook-ran\n");
        //CSJ,variable=previousCount,直前の件数
        var previousCount = Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT");
        //CSJ,variable=previousKey,直前のキー
        var previousKey = Environment.GetEnvironmentVariable("GIT_CONFIG_KEY_0");
        //CSJ,variable=previousValue,直前の値
        var previousValue = Environment.GetEnvironmentVariable("GIT_CONFIG_VALUE_0");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "injected.txt"), "synthetic");
            Check((await git.StageAsync(["injected.txt"])).Success, "stage for injected config test");
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "core.hooksPath");
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", hooks.Replace('\\', '/'));
            Check((await git.CommitAsync("config environment fixture")).Success, "commit with environment fixture");
            Check(!File.Exists(Path.Combine(root, "injected-hook-ran")), "Git executed inherited config-injected hook");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", previousCount);
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", previousKey);
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", previousValue);
        }
        var previousParameters = Environment.GetEnvironmentVariable("GIT_CONFIG_PARAMETERS");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "parameters.txt"), "synthetic");
            Check((await git.StageAsync(["parameters.txt"])).Success, "stage for parameters test");
            Environment.SetEnvironmentVariable("GIT_CONFIG_PARAMETERS", $"'core.hooksPath={hooks.Replace('\\', '/')}'");
            //CSJ,variable=probe,調査
            var probe = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            ConfigureFixtureGit(probe);
            probe.Environment["GIT_CONFIG_PARAMETERS"] = Environment.GetEnvironmentVariable("GIT_CONFIG_PARAMETERS");
            foreach (var arg in new[] { "config", "--get", "core.hooksPath" }) probe.ArgumentList.Add(arg);
            //CSJ,variable=process,プロセス
            using (var process = Process.Start(probe)!)
            {
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Check(process.ExitCode == 0 && (await output).Trim() == hooks.Replace('\\', '/'), "parameters injection fixture invalid: " + await error);
            }
            Check((await git.CommitAsync("config parameters fixture")).Success, "commit with parameters fixture");
            Check(!File.Exists(Path.Combine(root, "injected-hook-ran")), "Git executed GIT_CONFIG_PARAMETERS-injected hook");
        }
        finally { Environment.SetEnvironmentVariable("GIT_CONFIG_PARAMETERS", previousParameters); }
        await CommandAsync(root, "config", "--local", "core.hooksPath", hooks.Replace('\\', '/'));
        await File.WriteAllTextAsync(Path.Combine(root, "local-hook.txt"), "synthetic");
        Check((await git.StageAsync(["local-hook.txt"])).Success && (await git.CommitAsync("local hook fixture")).Success, "local hook commit");
        Check(!File.Exists(Path.Combine(root, "injected-hook-ran")), "reviewed Commit must not execute repository-configured hooks");
        Check((await git.DetectAsync()).Success && (await git.HistoryAsync()).StandardOutput.Contains("first commit"), "detect/history");
        Check((await git.CreateBranchAsync("feature/test")).Success && (await git.SwitchBranchAsync("feature/test")).Success, "branches");
        Check((await git.BranchesAsync()).StandardOutput.Contains("feature/test"), "branch list");
        await File.WriteAllTextAsync(Path.Combine(root, "one.txt"), "changed");
        Check((await git.StageAsync(["one.txt"])).Success && (await git.UnstageAsync(["one.txt"])).Success, "stage/unstage");
        Check((await git.StatusAsync()).StandardOutput.Contains("one.txt"), "status");
        try { await git.StageAsync(["../outside.txt"]); throw new Exception("traversal accepted"); } catch (ArgumentException) { }
        try { await git.CreateBranchAsync("--help"); throw new Exception("option injection accepted"); } catch (ArgumentException) { }
        //CSJ,variable=cancelled,キャンセル済み
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await git.StatusAsync(cancelled.Token); throw new Exception("cancel ignored"); } catch (OperationCanceledException) { }
        Console.WriteLine("Offline Git local operations passed.");
        await Program.RunNamedCaseAsync("Reviewed Commit preserves staged and unstaged changes across a ref compare-and-swap", () => VerifyReviewedCommitAsync(workRoot));
        await Program.RunNamedCaseAsync("Reviewed Commit rejects changed staging, branch moves and cancellation", () => VerifyCommitInterferenceAsync(workRoot));
        await Program.RunNamedCaseAsync("R22 Commit reports uncertainty when the verified Git child stops after publication", () => VerifyInterruptedCommitAsync(workRoot));
        await Program.RunNamedCaseAsync("GitHub scope and helper isolation preserve auth, permission and cancellation categories", () => VerifyGitHubProfileAsync(workRoot));
        await Program.RunNamedCaseAsync("GitHub access requires the same repository and exact locked origin as the approval", () => VerifyGitHubAccessApprovalAsync(workRoot));
        await Program.RunNamedCaseAsync("Stage validates the exact proposed tree before publishing the index", () => VerifyGuardedIndexAsync(workRoot));
    }

    private static async Task VerifyGuardedIndexAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = await MakeCommitFixtureAsync(workRoot, "guarded-index");
        //CSJ,variable=indexPath,インデックスパス
        var indexPath = Path.Combine(root, ".git", "index");
        //CSJ,variable=indexBefore,インデックス変更前
        var indexBefore = await File.ReadAllBytesAsync(indexPath);
        //CSJ,variable=text,テキスト
        var text = Path.Combine(root, "explicit.txt");
        await File.WriteAllTextAsync(text, "approved staging payload");
        var examined = false;
        var refuse = new GitRepository(root)
        {
            OnStageTreePrepared = async (tree, _) =>
            {
                examined = (await CommandAsync(root, "show", tree + ":explicit.txt")).Trim() == "approved staging payload";
                return new GitResult(1, "", "synthetic publication refusal", GitFailure.UnsafeOperation);
            },
        };
        var result = await refuse.StageAsync(["explicit.txt"]);
        Require(examined && !result.Success, "Stage did not inspect and refuse the exact tree");
        Require((await File.ReadAllBytesAsync(indexPath)).SequenceEqual(indexBefore), "refused stage changed the original index");
        Require(!File.Exists(indexPath + ".lock") && !Directory.EnumerateDirectories(Path.GetDirectoryName(indexPath)!, ".ymm-stage-*").Any(),
            "refused stage failed to clean its owned files");

        var accept = new GitRepository(root)
        {
            OnStageTreePrepared = async (_, _) =>
            {
                await File.WriteAllTextAsync(text, "later worktree edit");
                return null;
            },
        };
        Require((await accept.StageAsync(["explicit.txt"])).Success, "approved stage failed");
        Require((await CommandAsync(root, "show", ":explicit.txt")).Trim() == "approved staging payload" &&
            await File.ReadAllTextAsync(text) == "later worktree edit", "Stage did not publish exact reviewed index");
        //CSJ,variable=folder,フォルダー
        var folder = Directory.CreateDirectory(Path.Combine(root, "bulk")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "secret.txt"), "bulk staging must not run");
        Require(!(await accept.StageAsync(["bulk"])).Success, "Stage accidentally accepted a directory");
        //CSJ,variable=rootCommit,ルートコミット
        var rootCommit = Path.Combine(workRoot, "initial-index");
        Directory.CreateDirectory(rootCommit);
        //CSJ,variable=initial,初期
        var initial = new GitRepository(rootCommit);
        Require((await initial.InitializeAsync()).Success, "first stage fixture init");
        await File.WriteAllTextAsync(Path.Combine(rootCommit, "one.txt"), "initial payload");
        Require((await initial.StageAsync(["one.txt"])).Success && (await initial.UnstageAsync(["one.txt"])).Success,
            "could not unstage the selected first-commit path");
        Require((await CommandAsync(rootCommit, "ls-files")).Length == 0 && File.Exists(Path.Combine(rootCommit, "one.txt")),
            "first-commit unstage changed the worktree");
    }

    //CSJ,method=VerifyGitHubProfileAsync,GitHubプロファイルを検証する（非同期）
    private static async Task VerifyGitHubProfileAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(workRoot, "github-profile");
        Directory.CreateDirectory(root);
        var git = new GitRepository(root);
        Require((await git.InitializeAsync()).Success, "init GitHub settings fixture");
        //CSJ,variable=environment,環境
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GcM_TRACE_SECRETS"] = "synthetic",
            ["Gh_Token"] = "synthetic",
            ["DOTNET_STARTUP_HOOKS"] = "synthetic",
            ["CORECLR_PROFILER_PATH"] = "synthetic",
            ["APPDOMAIN_MANAGER_ASM"] = "synthetic",
            ["appdomain_manager_type"] = "synthetic",
            ["DevPath"] = "synthetic",
            ["bash_env"] = "synthetic",
            ["SSLKEYLOGFILE"] = "synthetic",
            ["https_proxy"] = "synthetic",
            ["nO_PrOxY"] = "synthetic",
            ["SystemRoot"] = "fixture",
        };
        GitRepository.RemoveGitHubChildOverrides(environment);
        Require(environment.Keys.SequenceEqual(["SystemRoot"]), "managed helper or shell startup environment was inherited");
        Require(GitRepository.IsSupportedGitHubEndpoint("https://github.com/Synthetic/repo-name.git"), "literal repository URL accepted");
        foreach (var uri in new[]
        {
            "https://github.com.evil.invalid/Synthetic/repo", "https://github.com:443/Synthetic/repo",
            "https://github.com/Synthetic/../repo", "https://github.com/Synthetic/%72epo",
            "https://github.com/Synthetic/repo?token=value", "https://github.com@evil.invalid/Synthetic/repo",
            "https://github.com/Synthetic/repo/extra", "https://github.com/Synthetic/..",
        })
        {
            Require(!GitRepository.IsSupportedGitHubEndpoint(uri), "non-literal repository URL accepted");
            Require((await git.ProbeGitHubAccessAsync(uri, GitHubAuthentication.CachedWindows)).Failure == GitFailure.UnsafeOperation,
                "unsupported endpoint reached the credential path");
        }
        //CSJ,variable=cancelled,キャンセル済み
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await git.ProbeGitHubAccessAsync("https://github.com/Synthetic/repo-name.git", GitHubAuthentication.CachedWindows, cancelled.Token);
            throw new InvalidOperationException("credential probe ignored pre-cancellation");
        }
        catch (OperationCanceledException) { }

        var script = Path.Combine(root, "manager.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nif [ \"$1\" != get ]; then printf invoked > helper-mutation-ran; fi\nprintf 'username=fixture\\npassword=synthetic-non-secret\\n'\n");
        foreach (var candidate in new[]
        {
            (Helper: (string?)null, Host: "github.com", Expected: 128),
            (Helper: script, Host: "github.com", Expected: 0),
            (Helper: script, Host: "other.invalid", Expected: 128),
        })
        {
            // This exercises Git's actual helper parser with a wholly local,
            // synthetic helper. No network connection or real credential is made.
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            ConfigureFixtureGit(start);
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment.Remove("SSH_ASKPASS");
            //CSJ,variable=arguments,引数一覧
            var arguments = GitRepository.BuildGitHubTransportSettings(
                ["-c", "credential.https://github.com.helper=!echo unsafe > unsafe-helper-ran"], candidate.Helper);
            foreach (var arg in arguments.Concat(["credential", "fill"])) start.ArgumentList.Add(arg);
            //CSJ,variable=process,プロセス
            using var process = Process.Start(start)!;
            //CSJ,variable=stdout,標準出力
            var stdout = process.StandardOutput.ReadToEndAsync();
            //CSJ,variable=stderr,標準エラー
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.BaseStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"protocol=https\nhost={candidate.Host}\npath=Synthetic/repo.git\n\n"));
            process.StandardInput.Close();
            await process.WaitForExitAsync();
            var output = await stdout;
            await stderr;
            Require(process.ExitCode == candidate.Expected && output.Contains("password=synthetic-non-secret") == (candidate.Expected == 0),
                "helper selection did not match the approved host or auth mode");
            Require(!File.Exists(Path.Combine(root, "unsafe-helper-ran")), "lower-priority credential helper was executed");
        }

        //CSJ,variable=action,アクション
        foreach (var action in new[] { "approve", "reject" })
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            ConfigureFixtureGit(start);
            foreach (var arg in GitRepository.BuildGitHubTransportSettings([], script).Concat(["credential", action]))
                start.ArgumentList.Add(arg);
            //CSJ,variable=process,プロセス
            using var process = Process.Start(start)!;
            //CSJ,variable=stdout,標準出力
            var stdout = process.StandardOutput.ReadToEndAsync();
            //CSJ,variable=stderr,標準エラー
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync("protocol=https\nhost=github.com\nusername=fixture\npassword=synthetic-non-secret\n\n");
            process.StandardInput.Close();
            await Task.WhenAll(process.WaitForExitAsync(), stdout, stderr);
            Require(process.ExitCode == 0 && !File.Exists(Path.Combine(root, "helper-mutation-ran")),
                "Git could mutate saved credentials through the read-only helper profile.");
        }

        foreach (var (reason, failure) in new[]
        {
            ("authentication failed", GitFailure.Authentication),
            ("The requested URL returned error: 401", GitFailure.Authentication),
            ("The requested URL returned error: 403", GitFailure.Permission),
            ("permission denied", GitFailure.Permission),
            ("The requested URL returned error: 301", GitFailure.RemoteRedirect),
            ("The requested URL returned error: 302", GitFailure.RemoteRedirect),
            ("The requested URL returned error: 303", GitFailure.RemoteRedirect),
            ("The requested URL returned error: 307", GitFailure.RemoteRedirect),
            ("The requested URL returned error: 308", GitFailure.RemoteRedirect),
            ("unable to update url base from redirection", GitFailure.RemoteRedirect),
        })
        {
            //CSJ,variable=marker,マーカー
            const string marker = "synthetic-private-diagnostic";
            //CSJ,variable=safe,安全
            var safe = GitRepository.SanitizeRemote(new(128, marker, reason + ": " + marker), false, null);
            Require(safe.Failure == failure && safe.StandardOutput == "" && !safe.StandardError.Contains(marker),
                "remote error was not classified or helper output leaked");
        }
    }

    //CSJ,method=VerifyGitHubAccessApprovalAsync,GitHubアクセス承認を検証する（非同期）
    private static async Task VerifyGitHubAccessApprovalAsync(string workRoot)
    {
        //CSJ,variable=approvedEndpoint,承認済みエンドポイント
        const string approvedEndpoint = "https://github.com/Synthetic/approved.git";
        //CSJ,variable=changedEndpoint,変更済みエンドポイント
        const string changedEndpoint = "https://github.com/Synthetic/changed.git";
        //CSJ,variable=root,ルート
        var root = Path.Combine(workRoot, "github-access");
        Directory.CreateDirectory(root);
        var git = new GitRepository(root);
        Require((await git.InitializeAsync()).Success, "init GitHub access approval fixture");
        await CommandAsync(root, "remote", "add", "origin", approvedEndpoint);
        //CSJ,variable=approved,承認済み
        var approved = (await git.InspectRemoteAsync(GitRemoteOperation.Fetch)).FetchTarget;
        Require(approved is not null && approved.Endpoint == approvedEndpoint, "capture the literal origin");
        var target = approved!;
        var config = Path.Combine(root, ".git", "config");

        //CSJ,variable=cancellation,キャンセル
        using var cancellation = new CancellationTokenSource();
        var reachedTransport = false;
        git.BeforeRemoteExecution = _ =>
        {
            reachedTransport = true;
            cancellation.Cancel();
            try
            {
                //CSJ,variable=writer,書き込み処理
                using var writer = File.Open(config, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                throw new InvalidOperationException("the approved origin config could be modified before the probe");
            }
            catch (IOException) { }
        };

        await CommandAsync(root, "remote", "set-url", "origin", changedEndpoint);
        //CSJ,variable=changed,変更済み
        var changed = await git.ProbeGitHubAccessAsync(target, GitHubAuthentication.Anonymous, cancellation.Token);
        Require(changed.Failure == GitFailure.UnsafeOperation && changed.StandardError.Contains("origin differs") && !reachedTransport,
            "changed origin reached the transport");

        await CommandAsync(root, "remote", "set-url", "origin", approvedEndpoint);
        await CommandAsync(root, "config", "--local", "--add", "remote.origin.url", changedEndpoint);
        //CSJ,variable=multiple,複数
        var multiple = await git.ProbeGitHubAccessAsync(target, GitHubAuthentication.Anonymous, cancellation.Token);
        Require(multiple.Failure == GitFailure.UnsafeOperation && !reachedTransport, "ambiguous origin reached the transport");
        await CommandAsync(root, "config", "--local", "--unset-all", "remote.origin.url");
        await CommandAsync(root, "config", "--local", "remote.origin.url", approvedEndpoint);
        try
        {
            await git.ProbeGitHubAccessAsync(target, GitHubAuthentication.Anonymous, cancellation.Token);
            throw new InvalidOperationException("approved probe ignored cancellation before network execution");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Require(reachedTransport, "unchanged origin did not reach the pre-network boundary");

        // The replacement has the same absolute path and literal URL, but not the approved identity.
        Directory.Move(root, root + "-original");
        Directory.CreateDirectory(root);
        git = new GitRepository(root) { BeforeRemoteExecution = _ => throw new InvalidOperationException("replacement reached the transport") };
        Require((await git.InitializeAsync()).Success, "init replacement GitHub access fixture");
        await CommandAsync(root, "remote", "add", "origin", approvedEndpoint);
        //CSJ,variable=replacement,置換
        var replacement = await git.ProbeGitHubAccessAsync(target, GitHubAuthentication.Anonymous);
        Require(replacement.Failure == GitFailure.UnsafeOperation && replacement.StandardError.Contains("its Git directory changed"),
            "same-path replacement did not invalidate the access approval");
    }

    private static async Task VerifyReviewedCommitAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = await MakeCommitFixtureAsync(workRoot, "commit-review");
        var git = new GitRepository(root);
        //CSJ,variable=changed,変更済み
        var changed = Path.Combine(root, "scène.txt");
        await File.WriteAllTextAsync(changed, "staged text");
        await File.WriteAllTextAsync(Path.Combine(root, "keep.txt"), "want to keep local");
        Require((await git.StageAsync(["scène.txt", "keep.txt"])).Success, "stage reviewed paths");
        Require((await git.UnstageAsync(["keep.txt"])).Success, "unstage independent path");
        //CSJ,variable=plan,計画
        var plan = (await git.InspectCommitAsync()).CommitTarget;
        Require(plan is not null && plan.Paths.SequenceEqual(["scène.txt"]), "exact staged path list");
        //CSJ,variable=index,インデックス
        var index = await File.ReadAllBytesAsync(Path.Combine(root, ".git", "index"));
        await File.WriteAllTextAsync(changed, "later unstaged text");
        await CommandAsync(root, "config", "--local", "commit.gpgSign", "true");
        var signingRequired = await git.CommitAsync(plan!, "must not bypass required signing");
        Require(!signingRequired.Success && signingRequired.StandardError.Contains("signing"), "required signing was silently bypassed");
        await CommandAsync(root, "config", "--local", "--unset", "commit.gpgSign");
        //CSJ,variable=commit,コミット
        var commit = await git.CommitAsync(plan!, "レビュー済み\n\nSecond paragraph");
        Require(commit.Success && (await CommandAsync(root, "rev-parse", "HEAD")).Trim() == commit.StandardOutput,
            "published commit OID");
        Require((await CommandAsync(root, "show", "HEAD:scène.txt")).Trim() == "staged text", "published exact staged contents");
        Require(await File.ReadAllTextAsync(changed) == "later unstaged text", "unstaged data retained");
        Require((await CommandAsync(root, "show", "-s", "--format=%B")).Contains("Second paragraph"), "multi-line message retained");
        Require((await File.ReadAllBytesAsync(Path.Combine(root, ".git", "index"))).SequenceEqual(index), "index bytes retained");
        Require(!File.Exists(Path.Combine(root, ".git", "index.lock")), "index lock released");
    }

    private static async Task VerifyCommitInterferenceAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = await MakeCommitFixtureAsync(workRoot, "commit-interference");
        var git = new GitRepository(root);
        var head = (await CommandAsync(root, "rev-parse", "HEAD")).Trim();
        //CSJ,variable=file,ファイル
        var file = Path.Combine(root, "scène.txt");
        await File.WriteAllTextAsync(file, "approved");
        Require((await git.StageAsync(["scène.txt"])).Success, "stage initial review");
        var old = (await git.InspectCommitAsync()).CommitTarget!;
        await File.WriteAllTextAsync(file, "different");
        Require((await git.StageAsync(["scène.txt"])).Success, "change index after review");
        Require(!(await git.CommitAsync(old, "must refuse stale review")).Success, "changed staged tree accepted");

        var current = (await git.InspectCommitAsync()).CommitTarget!;
        //CSJ,variable=cancel,キャンセル
        using var cancel = new CancellationTokenSource();
        var cancellable = new GitRepository(root) { BeforeCommitRefUpdate = cancel.Cancel };
        try
        {
            await cancellable.CommitAsync(current, "must cancel", cancel.Token);
            throw new InvalidOperationException("cancellation was ignored before publication");
        }
        catch (OperationCanceledException) { }
        Require((await CommandAsync(root, "rev-parse", "HEAD")).Trim() == head, "HEAD changed after rejection or cancel");
        Require(!File.Exists(Path.Combine(root, ".git", "index.lock")), "cancelled index lock released");
        Require(!Directory.EnumerateFiles(Path.Combine(root, ".git"), "ymm-index-*").Any(), "owned index snapshot was left behind");

        //CSJ,variable=mergeMarker,マージマーカー
        var mergeMarker = Path.Combine(root, ".git", "MERGE_HEAD");
        //CSJ,variable=merge,マージ
        var merge = new GitRepository(root) { BeforeCommitRefUpdate = () => File.WriteAllText(mergeMarker, head + "\n") };
        Require((await merge.CommitAsync(current, "must not publish during merge")).Failure == GitFailure.ConcurrentOperation,
            "merge started at the publication boundary was ignored");
        Require((await CommandAsync(root, "rev-parse", "HEAD")).Trim() == head && await File.ReadAllTextAsync(mergeMarker) == head + "\n",
            "merge evidence or branch ref was changed");
        File.Delete(mergeMarker);

        //CSJ,variable=commit,コミット
        var commit = (await CommandAsync(root, "commit-tree", current.TreeObjectId, "-p", head, "-m", "independent actor")).Trim();
        var raced = new GitRepository(root)
        {
            BeforeCommitRefUpdate = () =>
            {
                Require(CommandAsync(root, allowedExitCode: 1, "symbolic-ref", "HEAD", "refs/heads/interference")
                    .GetAwaiter().GetResult().Length == 0, "HEAD could be changed during Commit");
                CommandAsync(root, "update-ref", current.BranchRef, commit, head).GetAwaiter().GetResult();
            },
        };
        //CSJ,variable=rejected,拒否
        var rejected = await raced.CommitAsync(current, "must not overwrite");
        Require(rejected.Failure == GitFailure.ConcurrentOperation, "branch CAS accepted competing update");
        Require((await CommandAsync(root, "rev-parse", "HEAD")).Trim() == commit, "concurrent branch update preserved");
        Require((await CommandAsync(root, "symbolic-ref", "HEAD")).Trim() == current.BranchRef, "HEAD pin was ineffective");

        await File.WriteAllTextAsync(file, "new approved staging");
        Require((await git.StageAsync(["scène.txt"])).Success, "stage current data for raw ref verification");
        var actual = (await git.InspectCommitAsync()).CommitTarget!;
        const string alternate = "refs/heads/alternate";
        await CommandAsync(root, "update-ref", alternate, commit);
        var alias = new GitRepository(root)
        {
            BeforeCommitRefUpdate = () =>
                CommandAsync(root, "symbolic-ref", actual.BranchRef, alternate).GetAwaiter().GetResult(),
        };
        Require((await alias.CommitAsync(actual, "must preserve symbolic branch")).Failure == GitFailure.ConcurrentOperation,
            "ref CAS overwrote an alias with the same dereferenced parent");
        Require((await CommandAsync(root, "symbolic-ref", actual.BranchRef)).Trim() == alternate &&
                (await CommandAsync(root, "rev-parse", "HEAD")).Trim() == commit, "symbolic branch or its commit changed");
    }

    private static async Task<string> MakeCommitFixtureAsync(string workRoot, string name)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(workRoot, name);
        Directory.CreateDirectory(root);
        var git = new GitRepository(root);
        Require((await git.InitializeAsync()).Success, "init commit fixture");
        await CommandAsync(root, "config", "--local", "user.name", "Synthetic Tester");
        await CommandAsync(root, "config", "--local", "user.email", "synthetic@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(root, "keep.txt"), "original");
        await File.WriteAllTextAsync(Path.Combine(root, "scène.txt"), "original");
        Require((await git.StageAsync(["keep.txt", "scène.txt"])).Success, "prepare fixture first stage");
        Require((await git.CommitAsync("first reviewed fixture commit")).Success, "create reviewed root commit");
        return root;
    }

    //CSJ,method=VerifyInterruptedCommitAsync,中断済みコミットを検証する（非同期）
    private static async Task VerifyInterruptedCommitAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = await MakeCommitFixtureAsync(workRoot, "r22-commit-stop");
        await File.WriteAllTextAsync(Path.Combine(root, "scène.txt"), "staged fixture");
        var git = new GitRepository(root);
        Require((await git.StageAsync(["scène.txt"])).Success, "fixture stage failed");
        var target = (await git.InspectCommitAsync()).CommitTarget!;
        //CSJ,variable=indexPath,インデックスパス
        var indexPath = Path.Combine(root, ".git", "index");
        //CSJ,variable=indexBefore,インデックス変更前
        var indexBefore = await File.ReadAllBytesAsync(indexPath);
        //CSJ,variable=interrupted,中断済み
        var interrupted = false;
        var actor = new GitRepository(root)
        {
            AfterCommitAcknowledged = process =>
            {
                // Native Git acknowledged the ref update and is waiting for more input.
                process.Kill();
                interrupted = true;
            },
        };
        var result = await actor.CommitAsync(target, "synthetic acknowledged update");
        var actual = (await CommandAsync(root, "rev-parse", "HEAD")).Trim();
        //CSJ,variable=actualTree,実測値ツリー
        var actualTree = (await CommandAsync(root, "rev-parse", "HEAD^{tree}")).Trim();
        //CSJ,variable=indexAfter,インデックス変更後
        var indexAfter = await File.ReadAllBytesAsync(indexPath);
        Require(interrupted && result.Failure == GitFailure.OutcomeUnknown && actual != target.ParentObjectId &&
            actualTree == target.TreeObjectId && indexBefore.SequenceEqual(indexAfter), "interrupted native Commit outcome or data was lost");
    }

    private static Task<string> CommandAsync(string root, params string[] args) => CommandAsync(root, 0, args);

    private static async Task<string> CommandAsync(string root, int allowedExitCode, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        ConfigureFixtureGit(start);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync();
        //CSJ,variable=error,エラー
        var error = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync();
        Require(child.ExitCode == allowedExitCode, $"fixture {args[0]} returned {child.ExitCode}: {await error}");
        return await output;
    }

    private static void ConfigureFixtureGit(ProcessStartInfo start)
    {
        //CSJ,variable=key,キー
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        start.Environment["GIT_ALLOW_PROTOCOL"] = "";
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
