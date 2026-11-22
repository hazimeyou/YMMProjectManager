using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using YMMProjectManager.Infrastructure.Git;

//CSJ,class=GitRemoteTests,Gitリモートテスト
internal static class GitRemoteTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    public static async Task RunAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(workRoot, "git-remote"); Directory.CreateDirectory(root);
        var global = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        var system = Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", Path.Combine(root, "empty-global"));
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        try
        {
            //CSJ,method=Check,確認する
            static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
            //CSJ,variable=environment,環境
            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["GIT_ALLOW_PROTOCOL"] = "file:https",
                ["GIT_NO_LAZY_FETCH"] = "0",
            };
            GitRepository.SetTransportEnvironment(environment, allowedProtocol: null);
            Check(environment["GIT_ALLOW_PROTOCOL"] == "" && environment["GIT_NO_LAZY_FETCH"] == "1",
                "preview kept an inherited permission for implicit transport");
            GitRepository.SetTransportEnvironment(environment, "https");
            Check(environment["GIT_ALLOW_PROTOCOL"] == "", "explicit operation widened an inherited deny-all policy");
            environment["GIT_ALLOW_PROTOCOL"] = "file";
            GitRepository.SetTransportEnvironment(environment, "https");
            Check(environment["GIT_ALLOW_PROTOCOL"] == "", "explicit operation widened an inherited allowlist");
            environment["GIT_ALLOW_PROTOCOL"] = "file:https";
            GitRepository.SetTransportEnvironment(environment, "https");
            Check(environment["GIT_ALLOW_PROTOCOL"] == "https" && environment["GIT_NO_LAZY_FETCH"] == "1",
                "explicit operation lost its approved protocol or allowed lazy fetching");
            async Task<string> Command(string cwd, params string[] args)
            {
                var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in args) start.ArgumentList.Add(arg);
                //CSJ,variable=process,プロセス
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(); var stdout = await output; var stderr = await error;
                Check(process.ExitCode == 0, "fixture git: " + stderr); return stdout.Trim();
            }
            // A number embedded in a destination path must not be mistaken for an HTTP error.
            var source = Path.Combine(root, "source"); var bare = Path.Combine(root, "remote-403.git"); var clone = Path.Combine(root, "clone");
            Directory.CreateDirectory(source); var one = new GitRepository(source); Check((await one.InitializeAsync()).Success, "init");
            Check(!(await one.FetchAsync()).Success, "missing remote accepted");
            await Command(source, "config", "user.name", "Synthetic Tester"); await Command(source, "config", "user.email", "synthetic@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(source, "one.txt"), "one");
            Check((await one.StageAsync(["one.txt"])).Success && (await one.CommitAsync("first café")).Success, "initial commit");
            await Command(root, "init", "--bare", bare); await Command(source, "remote", "add", "origin", bare);
            //CSJ,variable=branch,ブランチ
            var branch = await Command(source, "symbolic-ref", "--short", "HEAD");
            //CSJ,variable=expectedCommit,期待値コミット
            var expectedCommit = await Command(source, "rev-parse", "HEAD");
            await Command(root, "--git-dir", bare, "symbolic-ref", "HEAD", "refs/heads/" + branch);
            var decoy = Path.Combine(root, "decoy.git");
            await Command(root, "init", "--bare", decoy);
            //CSJ,variable=oldCount,旧件数
            var oldCount = Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT");
            //CSJ,variable=oldKey,旧キー
            var oldKey = Environment.GetEnvironmentVariable("GIT_CONFIG_KEY_0");
            //CSJ,variable=oldValue,旧値
            var oldValue = Environment.GetEnvironmentVariable("GIT_CONFIG_VALUE_0");
            try
            {
                var redirected = new Uri(decoy).AbsoluteUri;
                Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
                Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", $"url.{redirected}.insteadOf");
                Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", bare);
                Check((await Command(source, "remote", "get-url", "origin")) == redirected, "environment rewrite fixture invalid");
                var actual = await one.OriginAsync();
                Check(actual.Success && actual.StandardOutput == bare, "origin followed inherited config rewrite");
            }
            finally
            {
                Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", oldCount);
                Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", oldKey);
                Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", oldValue);
            }
            one.BeforeRemoteExecution = _ =>
            {
                var start = new ProcessStartInfo("git") { WorkingDirectory = source, UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { "remote", "set-url", "origin", decoy }) start.ArgumentList.Add(arg);
                //CSJ,variable=process,プロセス
                using var process = Process.Start(start)!; process.WaitForExit();
                Check(process.ExitCode == 0, "change remote fixture");
                var switchHead = new ProcessStartInfo("git") { WorkingDirectory = source, UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { "symbolic-ref", "HEAD", "refs/heads/concurrent-unborn" }) switchHead.ArgumentList.Add(arg);
                //CSJ,variable=headProcess,HEADプロセス
                using var headProcess = Process.Start(switchHead)!; headProcess.WaitForExit();
                Check(headProcess.ExitCode == 0, "change HEAD fixture");
            };
            //CSJ,variable=initialPush,初期プッシュ
            var initialPush = await one.PushAsync();
            Check(initialPush.Success, $"initial push failed ({initialPush.Failure}, exit {initialPush.ExitCode}): {initialPush.StandardError}");
            one.BeforeRemoteExecution = null;
            Check(await Command(root, "--git-dir", bare, "rev-parse", "refs/heads/" + branch) == expectedCommit, "push used changed HEAD");
            await Command(source, "symbolic-ref", "HEAD", "refs/heads/" + branch);
            Check(!Directory.EnumerateFiles(Path.Combine(decoy, "refs", "heads"), "*", SearchOption.AllDirectories).Any(), "push followed changed origin");
            await Command(source, "remote", "set-url", "origin", bare);
            //CSJ,variable=initialClone,初期クローン
            var initialClone = await GitRepository.CloneAsync(bare, clone);
            Check(initialClone.Success, $"clone failed ({initialClone.Failure}, exit {initialClone.ExitCode}): {initialClone.StandardError}");
            var two = new GitRepository(clone);
            await Command(clone, "config", "user.name", "Synthetic Tester"); await Command(clone, "config", "user.email", "synthetic@example.invalid");
            await Command(clone, "tag", branch);
            await File.WriteAllTextAsync(Path.Combine(clone, "two.txt"), "two");
            Check((await two.StageAsync(["two.txt"])).Success && (await two.CommitAsync("second")).Success && (await two.PushAsync()).Success, "clone push");
            await Command(source, "branch", "race-side");
            var pullReachedRemoteExecution = false;
            one.BeforeRemoteExecution = _ => pullReachedRemoteExecution = true;
            var unapprovedBeforeInspection = await one.PullAsync();
            one.BeforeRemoteExecution = null;
            Check(unapprovedBeforeInspection.Failure == GitFailure.UnsafeOperation && !pullReachedRemoteExecution,
                "unapproved pull reached remote preflight");
            await Command(source, "switch", "race-side");
            Check((await one.InspectPullAsync()).State == GitPullState.TrackingUnavailable, "unknown tracking ref treated as pull candidate");
            await Command(source, "switch", branch);
            //CSJ,variable=cancelledPull,キャンセル済みプル
            using (var cancelledPull = new CancellationTokenSource())
            {
                cancelledPull.Cancel();
                try { await one.PullAsync(cancelledPull.Token); throw new Exception("pull cancellation ignored"); }
                catch (OperationCanceledException) { }
                try { await one.InspectPullAsync(cancelledPull.Token); throw new Exception("pull inspection ignored cancellation"); }
                catch (OperationCanceledException) { }
            }
            Check((await one.FetchAsync()).Success, "fetch");
            //CSJ,variable=originalIndex,元のインデックス
            var originalIndex = SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(source, ".git", "index")));
            //CSJ,variable=currentLocal,現在のローカル
            var currentLocal = await Command(source, "rev-parse", "HEAD");
            //CSJ,variable=currentUpstream,現在の上流
            var currentUpstream = await Command(source, "rev-parse", "refs/remotes/origin/" + branch);
            //CSJ,variable=checkPull,確認プル
            var checkPull = await one.InspectPullAsync();
            Check(checkPull is { State: GitPullState.FastForwardCandidate } && checkPull.BranchRef == "refs/heads/" + branch &&
                checkPull.LocalObjectId == currentLocal && checkPull.TrackingRef == "refs/remotes/origin/" + branch &&
                checkPull.TrackingObjectId == currentUpstream, "incorrect pull assessment");
            Check(await Command(source, "rev-parse", "HEAD") == currentLocal &&
                await Command(source, "rev-parse", "refs/remotes/origin/" + branch) == currentUpstream &&
                SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(source, ".git", "index"))).SequenceEqual(originalIndex) &&
                !File.Exists(Path.Combine(source, "two.txt")), "pull assessment modified refs, index or worktree");
            await Program.RunNamedCaseAsync("Pull inspection follows the configured remote tracking branch", async () =>
            {
                const string alternateRemote = "r16-alternate";
                const string firstTrackingRef = "refs/remotes/" + alternateRemote + "/server";
                //CSJ,variable=secondTrackingRef,2番目追跡参照
                const string secondTrackingRef = "refs/remotes/" + alternateRemote + "/other";
                //CSJ,variable=remoteSetting,リモート設定
                var remoteSetting = $"branch.{branch}.remote";
                //CSJ,variable=mergeSetting,マージ設定
                var mergeSetting = $"branch.{branch}.merge";
                await Command(source, "config", "remote." + alternateRemote + ".fetch", "+refs/heads/*:refs/remotes/" + alternateRemote + "/*");
                await Command(source, "config", remoteSetting, alternateRemote);
                await Command(source, "config", mergeSetting, "refs/heads/server");
                await Command(source, "update-ref", firstTrackingRef, currentLocal);
                await Command(source, "update-ref", secondTrackingRef, currentLocal);
                try
                {
                    var configured = await one.InspectPullAsync();
                    Check(configured.State == GitPullState.UpToDate &&
                        configured.TrackingRef == firstTrackingRef && configured.TrackingObjectId == currentLocal,
                        "inspection selected origin instead of the configured upstream");

                    one.BeforePullInspectionRecheck = () =>
                        Command(source, "config", mergeSetting, "refs/heads/other").GetAwaiter().GetResult();
                    Check((await one.InspectPullAsync()).State == GitPullState.Changed,
                        "inspection missed a configured reference change with the same commit id");
                    one.BeforePullInspectionRecheck = null;

                    await Command(source, "config", mergeSetting, "refs/heads/not-fetched");
                    //CSJ,variable=missing,欠落
                    var missing = await one.InspectPullAsync();
                    Check(missing.State == GitPullState.TrackingUnavailable &&
                        missing.TrackingRef == "refs/remotes/" + alternateRemote + "/not-fetched",
                        "missing configured tracking ref was replaced with origin fallback");

                    await Command(source, "config", "--add", mergeSetting, "refs/heads/server");
                    Check((await one.InspectPullAsync()).State == GitPullState.Unavailable,
                        "inspection selected among multiple configured merge refs");
                }
                finally
                {
                    one.BeforePullInspectionRecheck = null;
                    await Command(source, "config", "--unset-all", mergeSetting);
                    await Command(source, "config", "--unset-all", remoteSetting);
                    await Command(source, "config", "--remove-section", "remote." + alternateRemote);
                }
            });
            await Program.RunNamedCaseAsync("Pull inspection rejects a moved or deleted local tracking ref", async () =>
            {
                //CSJ,variable=trackingRef,追跡参照
                var trackingRef = "refs/remotes/origin/" + branch;
                //CSJ,variable=upstreamRef,上流参照
                var upstreamRef = "refs/heads/" + branch;

                try
                {
                    await Command(root, "--git-dir", bare, "update-ref", upstreamRef, currentLocal, currentUpstream);
                    //CSJ,variable=cached,キャッシュ済み
                    var cached = await one.InspectPullAsync();
                    Check(cached.State == GitPullState.FastForwardCandidate && cached.TrackingObjectId == currentUpstream,
                        "inspection unexpectedly contacted the remote or changed the cached ref");

                    one.BeforePullInspectionRecheck = () =>
                        Command(source, "update-ref", trackingRef, currentLocal, currentUpstream).GetAwaiter().GetResult();
                    Check((await one.InspectPullAsync()).State == GitPullState.Changed &&
                        await Command(source, "rev-parse", trackingRef) == currentLocal,
                        "inspection missed or overwrote a moved tracking ref");
                    one.BeforePullInspectionRecheck = null;
                    Check((await one.InspectPullAsync()).State == GitPullState.UpToDate, "next inspection ignored moved tracking ref");

                    await Command(root, "--git-dir", bare, "update-ref", "-d", upstreamRef, currentLocal);
                    one.BeforePullInspectionRecheck = () =>
                        Command(source, "update-ref", "-d", trackingRef, currentLocal).GetAwaiter().GetResult();
                    Check((await one.InspectPullAsync()).State == GitPullState.Changed, "inspection missed a deleted tracking ref");
                    one.BeforePullInspectionRecheck = null;
                    Check((await one.InspectPullAsync()).State == GitPullState.TrackingUnavailable &&
                        await Command(source, "for-each-ref", "--format=%(refname)", trackingRef) == "",
                        "inspection recreated or ignored the deletion");

                    Check(await Command(source, "rev-parse", "HEAD") == currentLocal &&
                        SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(source, ".git", "index"))).SequenceEqual(originalIndex) &&
                        !File.Exists(Path.Combine(source, "two.txt")), "tracking change inspection modified HEAD, index or working tree");
                }
                finally
                {
                    one.BeforePullInspectionRecheck = null;
                    await Command(root, "--git-dir", bare, "update-ref", upstreamRef, currentUpstream);
                    await Command(source, "update-ref", trackingRef, currentUpstream);
                }
            });
            one.BeforePullInspectionRecheck = () =>
            {
                File.WriteAllText(Path.Combine(source, ".git", "HEAD"), "ref: refs/heads/race-side\n");
            };
            Check((await one.InspectPullAsync()).State == GitPullState.Changed, "stale inspection did not detect a changed HEAD");
            one.BeforePullInspectionRecheck = null;
            await Command(source, "switch", branch);
            await Program.RunNamedCaseAsync("Pull inspection refuses active operations and unverifiable index entries", async () =>
            {
                //CSJ,variable=gitDirectory,Git ディレクトリ
                var gitDirectory = Path.Combine(source, ".git");
                //CSJ,variable=indexPath,インデックスパス
                var indexPath = Path.Combine(gitDirectory, "index");
                //CSJ,variable=indexLock,インデックスロック
                var indexLock = indexPath + ".lock";
                var tracked = Path.Combine(source, "one.txt");
                //CSJ,variable=candidatePath,候補パス
                var candidatePath = Path.Combine(source, "two.txt");
                //CSJ,variable=branchLock,ブランチロック
                var branchLock = Path.Combine(gitDirectory, "refs", "heads", branch) + ".lock";
                //CSJ,variable=mergeMarker,マージマーカー
                var mergeMarker = Path.Combine(gitDirectory, "MERGE_HEAD");

                //CSJ,variable=marker,マーカー
                foreach (var marker in new[] { indexLock, branchLock, mergeMarker })
                {
                    await File.WriteAllTextAsync(marker, currentLocal);
                    try
                    {
                        Check((await one.InspectPullAsync()).State == GitPullState.OperationInProgress,
                            "a lock or in-progress operation was ignored");
                        Check(await File.ReadAllTextAsync(marker) == currentLocal, "inspection changed an operation marker");
                    }
                    finally { File.Delete(marker); }
                }

                one.BeforePullInspectionRecheck = () => File.WriteAllText(indexLock, "appeared");
                try
                {
                    Check((await one.InspectPullAsync()).State == GitPullState.Changed,
                        "inspection missed a lock acquired during assessment");
                }
                finally
                {
                    one.BeforePullInspectionRecheck = null;
                    File.Delete(indexLock);
                }

                //CSJ,variable=flag,フラグ
                foreach (var flag in new[] { "assume-unchanged", "skip-worktree" })
                {
                    await Command(source, "update-index", "--" + flag, "one.txt");
                    try
                    {
                        await File.WriteAllTextAsync(tracked, "hidden edit");
                        Check((await one.InspectPullAsync()).State == GitPullState.IndexNotVerifiable,
                            "inspection accepted an index flag that can conceal worktree edits");
                        Check(await File.ReadAllTextAsync(tracked) == "hidden edit", "inspection overwrote hidden worktree contents");
                    }
                    finally
                    {
                        await Command(source, "update-index", "--no-" + flag, "one.txt");
                        await File.WriteAllTextAsync(tracked, "one");
                    }
                }

                await Command(source, "config", "--local", "filter.fixture.clean", "fixture-configuration-only");
                try
                {
                    Check((await one.InspectPullAsync()).State == GitPullState.IndexNotVerifiable,
                        "inspection accepted a worktree with configured filters");
                }
                finally { await Command(source, "config", "--local", "--unset", "filter.fixture.clean"); }

                await Command(source, "update-index", "--add", "--cacheinfo", $"160000,{currentLocal},fixture-module");
                try
                {
                    Check((await one.InspectPullAsync()).State == GitPullState.IndexNotVerifiable,
                        "inspection accepted an unverified submodule");
                }
                finally { await Command(source, "update-index", "--force-remove", "--", "fixture-module"); }

                //CSJ,variable=indexContent,インデックス内容
                var indexContent = await File.ReadAllBytesAsync(indexPath);
                await File.WriteAllTextAsync(tracked, "first staged version");
                await Command(source, "add", "--", "one.txt");
                //CSJ,variable=stagedStatus,準備済みの状態
                var stagedStatus = await Command(source, "--no-optional-locks", "status", "--porcelain");
                one.BeforePullInspectionRecheck = () =>
                {
                    File.WriteAllText(tracked, "second staged version");
                    Command(source, "add", "--", "one.txt").GetAwaiter().GetResult();
                };
                try
                {
                    Check((await one.InspectPullAsync()).State == GitPullState.Changed &&
                        await Command(source, "--no-optional-locks", "status", "--porcelain") == stagedStatus,
                        "inspection missed an index change with the same porcelain status");
                }
                finally
                {
                    one.BeforePullInspectionRecheck = null;
                    await File.WriteAllTextAsync(tracked, "one");
                    await File.WriteAllBytesAsync(indexPath, indexContent);
                }

                //CSJ,variable=interrupted,中断済み
                using (var interrupted = new CancellationTokenSource())
                {
                    one.BeforePullInspectionRecheck = interrupted.Cancel;
                    try
                    {
                        await one.InspectPullAsync(interrupted.Token);
                        throw new InvalidOperationException("inspection ignored cancellation during recheck");
                    }
                    catch (OperationCanceledException) { }
                    finally { one.BeforePullInspectionRecheck = null; }
                }

                try
                {
                    await File.WriteAllTextAsync(indexPath, "synthetic invalid index");
                    Check((await one.InspectPullAsync()).State == GitPullState.Unavailable &&
                        await File.ReadAllTextAsync(indexPath) == "synthetic invalid index", "inspection accepted or rewrote invalid index");
                }
                finally { await File.WriteAllBytesAsync(indexPath, indexContent); }

                await File.WriteAllTextAsync(candidatePath, "keep untracked collision");
                Check((await one.InspectPullAsync()).State == GitPullState.Dirty &&
                    await File.ReadAllTextAsync(candidatePath) == "keep untracked collision", "untracked collision was ignored or lost");
                File.Delete(candidatePath);
            });
            await Program.RunNamedCaseAsync("Pull inspection protects ignored paths that overlap the incoming tree", async () =>
            {
                var exclude = Path.Combine(source, ".git", "info", "exclude");
                var priorExclude = await File.ReadAllBytesAsync(exclude);
                //CSJ,variable=collision,衝突
                var collision = Path.Combine(source, "two.txt");
                //CSJ,variable=nested,入れ子
                var nested = Path.Combine(collision, "keep.txt");
                var unrelated = Path.Combine(source, "notes.txt");
                const string contents = "user-owned synthetic content";
                try
                {
                    await File.AppendAllTextAsync(exclude, "\n/two.txt\n/notes.txt\n");
                    await File.WriteAllTextAsync(unrelated, contents);
                    Check((await one.InspectPullAsync()).State == GitPullState.FastForwardCandidate,
                        "unrelated ignored file prevented assessment");

                    await File.WriteAllTextAsync(collision, contents);
                    Check((await one.InspectPullAsync()).State == GitPullState.IgnoredPathCollision &&
                        await File.ReadAllTextAsync(collision) == contents,
                        "ignored incoming file collision was accepted or changed");
                    File.Delete(collision);

                    Directory.CreateDirectory(collision);
                    await File.WriteAllTextAsync(nested, contents);
                    Check((await one.InspectPullAsync()).State == GitPullState.IgnoredPathCollision &&
                        await File.ReadAllTextAsync(nested) == contents,
                        "ignored directory overlapping an incoming file was accepted or changed");
                    File.Delete(nested);
                    Directory.Delete(collision);

                    one.BeforePullInspectionRecheck = () => File.WriteAllText(collision, contents);
                    Check((await one.InspectPullAsync()).State == GitPullState.Changed &&
                        await File.ReadAllTextAsync(collision) == contents,
                        "ignored collision created during inspection was missed");
                }
                finally
                {
                    one.BeforePullInspectionRecheck = null;
                    if (File.Exists(nested)) File.Delete(nested);
                    if (Directory.Exists(collision)) Directory.Delete(collision);
                    if (File.Exists(collision)) File.Delete(collision);
                    File.Delete(unrelated);
                    await File.WriteAllBytesAsync(exclude, priorExclude);
                }
            });
            await Program.RunNamedCaseAsync("Branch switch preserves an ignored file that overlaps the target tree", async () =>
            {
                //CSJ,variable=targetBranch,対象ブランチ
                const string targetBranch = "r18-ignore-target";
                var exclude = Path.Combine(source, ".git", "info", "exclude");
                var priorExclude = await File.ReadAllBytesAsync(exclude);
                //CSJ,variable=path,パス
                var path = Path.Combine(source, "two.txt");
                //CSJ,variable=index,インデックス
                var index = Path.Combine(source, ".git", "index");
                const string contents = "protected synthetic ignored material";

                await Command(source, "branch", targetBranch, "refs/remotes/origin/" + branch);
                //CSJ,variable=oldHead,旧HEAD
                var oldHead = await Command(source, "rev-parse", "HEAD");
                //CSJ,variable=oldIndexHash,旧インデックスハッシュ
                var oldIndexHash = SHA256.HashData(await File.ReadAllBytesAsync(index));
                try
                {
                    await File.AppendAllTextAsync(exclude, "\n/two.txt\n");
                    await File.WriteAllTextAsync(path, contents);
                    Check(await Command(source, "check-ignore", "two.txt") == "two.txt", "branch-switch ignore fixture invalid");

                    var switched = await one.SwitchBranchAsync(targetBranch);
                    Check(!switched.Success, "branch switch reported success despite an ignored path collision");
                    Check(await File.ReadAllTextAsync(path) == contents, "branch switch changed ignored file contents");
                    Check(await Command(source, "symbolic-ref", "--short", "HEAD") == branch &&
                        await Command(source, "rev-parse", "HEAD") == oldHead, "ref changed after a refused branch switch");
                    Check(SHA256.HashData(await File.ReadAllBytesAsync(index)).SequenceEqual(oldIndexHash),
                        "index changed after a refused branch switch");

                    File.Delete(path);
                    Check((await one.SwitchBranchAsync(targetBranch)).Success &&
                        await File.ReadAllTextAsync(path) == "two", "branch switch without collision failed");
                }
                finally
                {
                    if (await Command(source, "symbolic-ref", "--short", "HEAD") == targetBranch)
                        await Command(source, "switch", branch);
                    File.Delete(path);
                    await File.WriteAllBytesAsync(exclude, priorExclude);
                }
            });
            await Program.RunNamedCaseAsync("Git status and history preserve Unicode outside a UTF-8 console", async () =>
            {
                //CSJ,variable=name,名前
                const string name = "素材の候補.txt";
                //CSJ,variable=path,パス
                var path = Path.Combine(source, name);
                //CSJ,variable=previousEncoding,直前のエンコード
                var previousEncoding = Console.OutputEncoding;
                await File.WriteAllTextAsync(path, "synthetic");
                await Command(source, "config", "i18n.logOutputEncoding", "ISO-8859-1");
                try
                {
                    Console.OutputEncoding = Encoding.Latin1;
                    //CSJ,variable=status,状態
                    var status = await one.StatusAsync();
                    Check(status.Success && status.StandardOutput.Split('\0').Contains("?? " + name),
                        "Git's UTF-8 path was decoded using the console encoding");
                    //CSJ,variable=history,履歴
                    var history = await one.HistoryAsync();
                    Check(history.Success && history.StandardOutput.Contains("first café", StringComparison.Ordinal),
                        "Git history encoding was affected by repository configuration");
                }
                finally
                {
                    Console.OutputEncoding = previousEncoding;
                    await Command(source, "config", "--unset", "i18n.logOutputEncoding");
                    File.Delete(path);
                }
            });
            var unapprovedPull = await one.PullAsync();
            Check(!unapprovedPull.Success && unapprovedPull.Failure == GitFailure.UnsafeOperation, "pull must fail closed");
            await Command(source, "merge", "--ff-only", "refs/remotes/origin/" + branch);
            Check(await Command(source, "rev-parse", "HEAD") == await Command(clone, "rev-parse", "HEAD"), "heads differ");
            Check((await one.InspectPullAsync()).State == GitPullState.UpToDate, "matching branches classified as different");
            await File.WriteAllTextAsync(Path.Combine(source, "local.txt"), "local");
            Check((await one.StageAsync(["local.txt"])).Success && (await one.CommitAsync("local")).Success, "local commit");
            Check((await one.InspectPullAsync()).State == GitPullState.LocalAhead, "local-ahead branch classified as pull candidate");
            await File.WriteAllTextAsync(Path.Combine(clone, "remote.txt"), "remote");
            Check((await two.StageAsync(["remote.txt"])).Success && (await two.CommitAsync("remote")).Success && (await two.PushAsync()).Success, "remote commit");
            var prior = await Command(source, "rev-parse", "HEAD");
            var remoteBeforeRejectedPush = await Command(root, "--git-dir", bare, "rev-parse", "refs/heads/" + branch);
            //CSJ,variable=rejectedPush,拒否プッシュ
            var rejectedPush = await one.PushAsync(); var rejectedPull = await one.PullAsync();
            Check(!rejectedPush.Success && !rejectedPull.Success, "divergence accepted");
            Check(rejectedPush.Failure == GitFailure.Divergence && rejectedPull.Failure == GitFailure.UnsafeOperation, $"failure classification: push={rejectedPush.Failure}, pull={rejectedPull.Failure}");
            Check((await one.FetchAsync()).Success, "offline divergence fetch");
            Check((await one.InspectPullAsync()).State == GitPullState.Diverged, "diverged branches classified as pull candidate");
            await Program.RunNamedCaseAsync("Pull assessment refuses repository grafts and discards inherited ancestry overrides", async () =>
            {
                var remoteTip = await Command(source, "rev-parse", "refs/remotes/origin/" + branch);
                var grafts = Path.Combine(source, ".git", "info", "grafts");
                var inherited = Path.Combine(root, "inherited-grafts");
                await File.WriteAllTextAsync(inherited, $"{remoteTip} {prior}\n");
                var oldGraftPath = Environment.GetEnvironmentVariable("GIT_GRAFT_FILE");
                try
                {
                    Environment.SetEnvironmentVariable("GIT_GRAFT_FILE", inherited);
                    await Command(source, "-c", "advice.graftFileDeprecated=false", "merge-base", "--is-ancestor", prior, remoteTip);
                    Check((await one.InspectPullAsync()).State == GitPullState.Diverged,
                        "inherited grafts reclassified diverged history");
                    Environment.SetEnvironmentVariable("GIT_GRAFT_FILE", null);

                    await File.WriteAllTextAsync(grafts, $"{remoteTip} {prior}\n");
                    await Command(source, "-c", "advice.graftFileDeprecated=false", "merge-base", "--is-ancestor", prior, remoteTip);
                    Check((await one.InspectPullAsync()).State == GitPullState.HistoryNotVerifiable,
                        "repository grafts reclassified diverged history");
                    File.Delete(grafts);

                    one.BeforePullInspectionRecheck = () => File.WriteAllText(grafts, $"{remoteTip} {prior}\n");
                    Check((await one.InspectPullAsync()).State == GitPullState.Changed,
                        "inspection accepted a graft added between observations");
                }
                finally
                {
                    one.BeforePullInspectionRecheck = null;
                    Environment.SetEnvironmentVariable("GIT_GRAFT_FILE", oldGraftPath);
                    File.Delete(grafts);
                }
                Check(await Command(source, "rev-parse", "HEAD") == prior &&
                    await Command(source, "rev-parse", "refs/remotes/origin/" + branch) == remoteTip,
                    "graft inspection changed a ref");
            });
            Check(await Command(source, "rev-parse", "HEAD") == prior, "divergence changed local head");
            Check(await Command(root, "--git-dir", bare, "rev-parse", "refs/heads/" + branch) == remoteBeforeRejectedPush, "divergence changed remote head");
            await Command(source, "remote", "set-url", "origin", "https://user:secret@example.invalid/repo");
            var unsafeOrigin = await one.OriginAsync();
            Check(!unsafeOrigin.Success && !unsafeOrigin.StandardOutput.Contains("secret") && !unsafeOrigin.StandardError.Contains("secret"), "credential disclosure");
            await Command(source, "remote", "set-url", "origin", "https://alpha.invalid/repo");
            await Command(source, "config", "--local", "url.https://beta.invalid/.insteadOf", "https://alpha.invalid/");
            await Command(source, "config", "--local", "url.https://gamma.invalid/.insteadOf", "https://beta.invalid/");
            var once = await Command(source, "remote", "get-url", "origin");
            var twice = await Command(source, "ls-remote", "--get-url", once);
            Check(once == "https://beta.invalid/repo" && twice == "https://gamma.invalid/repo", "rewrite fixture changed");
            var rejectedRewrite = await one.FetchAsync();
            Check(!rejectedRewrite.Success && rejectedRewrite.Failure == GitFailure.UnsafeOperation, "fetch should refuse a second URL rewrite before remote execution");
            await Command(source, "remote", "set-url", "origin", bare);
            await Command(source, "config", "--local", "--remove-section", "url.https://beta.invalid/");
            await Command(source, "config", "--local", "--remove-section", "url.https://gamma.invalid/");
            //CSJ,variable=inspection,点検
            var inspection = await one.InspectRemoteAsync(GitRemoteOperation.Fetch);
            Check(inspection.Success && inspection.StandardOutput == bare && inspection.FetchTarget is { } inspectedFetch &&
                inspectedFetch.Endpoint == bare && inspectedFetch.RepositoryRoot == source, "trusted inspection did not identify the fetch target");
            Check((await two.FetchTrustedAsync(inspection.FetchTarget!)).Failure == GitFailure.UnsafeOperation,
                "fetch approval was accepted from a different checkout");

            //CSJ,variable=identityRoot,識別情報ルート
            var identityRoot = Path.Combine(root, "fetch-identity");
            //CSJ,variable=firstRoot,最初ルート
            var firstRoot = Path.Combine(root, "fetch-retained");
            var competingRoot = Path.Combine(root, "fetch-other");
            var movedDuringFetch = Path.Combine(root, "fetch-moved");
            await Command(root, "init", identityRoot);
            await Command(root, "init", competingRoot);
            await Command(identityRoot, "remote", "add", "origin", bare);
            await Command(competingRoot, "remote", "add", "origin", bare);
            //CSJ,variable=identityRepository,識別情報リポジトリ
            var identityRepository = new GitRepository(identityRoot);
            //CSJ,variable=firstApproval,最初承認
            var firstApproval = (await identityRepository.InspectRemoteAsync(GitRemoteOperation.Fetch)).FetchTarget
                ?? throw new InvalidOperationException("missing first repository identity");
            Directory.Move(identityRoot, firstRoot);
            Directory.Move(competingRoot, identityRoot);
            Check((await identityRepository.FetchTrustedAsync(firstApproval)).Failure == GitFailure.UnsafeOperation,
                "same path and endpoint were accepted for a replaced checkout");
            Check((await Command(identityRoot, "for-each-ref", "--format=%(refname)", "refs/remotes")).Length == 0,
                "stale approval modified the replacement checkout");
            //CSJ,variable=replacementApproval,置換承認
            var replacementApproval = (await identityRepository.InspectRemoteAsync(GitRemoteOperation.Fetch)).FetchTarget
                ?? throw new InvalidOperationException("missing replacement identity");
            identityRepository.BeforeRemoteExecution = _ =>
            {
                try { Directory.Move(identityRoot, movedDuringFetch); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            };
            Check((await identityRepository.FetchTrustedAsync(replacementApproval)).Success && !Directory.Exists(movedDuringFetch),
                "approved fetch failed or allowed the repository directory to move during execution");
            Check(await Command(identityRoot, "rev-parse", "refs/remotes/origin/" + branch) == remoteBeforeRejectedPush,
                "approved fetch failed to write the expected tracking ref");
            identityRepository.BeforeRemoteExecution = null;
            Directory.Move(Path.Combine(identityRoot, ".git"), Path.Combine(identityRoot, ".git-retained"));
            Directory.Move(Path.Combine(firstRoot, ".git"), Path.Combine(identityRoot, ".git"));
            Check((await identityRepository.FetchTrustedAsync(replacementApproval)).Failure == GitFailure.UnsafeOperation,
                "unchanged worktree path concealed a replacement Git directory");
            Check((await Command(identityRoot, "for-each-ref", "--format=%(refname)", "refs/remotes")).Length == 0,
                "stale approval modified the replacement Git directory");
            await Command(source, "remote", "set-url", "--push", "origin", decoy);
            //CSJ,variable=pushInspection,プッシュ点検
            var pushInspection = await one.InspectRemoteAsync(GitRemoteOperation.Push);
            Check(pushInspection.Success && pushInspection.StandardOutput == decoy, "trusted inspection did not distinguish push URL");
            Check((await one.PushTrustedAsync(bare)).Failure == GitFailure.UnsafeOperation, "push accepted a different endpoint");
            Check(!Directory.EnumerateFiles(Path.Combine(decoy, "refs", "heads"), "*", SearchOption.AllDirectories).Any(), "push mismatch updated unapproved remote");
            await Command(source, "config", "--local", "--unset-all", "remote.origin.pushurl");
            //CSJ,variable=trustedFetch,信頼済みフェッチ
            var trustedFetch = await one.FetchTrustedAsync(bare);
            Check(trustedFetch.Success && trustedFetch.RemoteDiagnostic is { Operation: "Fetch" }, "trusted fetch did not complete or report diagnostics");
            Check((await two.PushTrustedAsync(bare)).Success, "trusted non-force push");
            //CSJ,variable=approval,承認
            var approval = (await two.InspectRemoteAsync(GitRemoteOperation.Push)).PushTarget
                ?? throw new InvalidOperationException("push inspection did not return a target");
            var cloneTip = await Command(clone, "rev-parse", "HEAD");
            Check(approval.Endpoint == bare && approval.BranchRef == "refs/heads/" + branch &&
                approval.ObjectId == cloneTip && approval.RepositoryRoot == clone, "push inspection identified the wrong target");
            Check((await one.PushTrustedAsync(approval)).Failure == GitFailure.UnsafeOperation, "push approval reused for another repository");

            var originalPushCheckout = Path.Combine(root, "push-identity");
            var retainedPushCheckout = Path.Combine(root, "push-retained");
            var alternatePushCheckout = Path.Combine(root, "push-other");
            Check((await GitRepository.CloneTrustedAsync(bare, originalPushCheckout)).Success, "first push identity checkout");
            Check((await GitRepository.CloneTrustedAsync(bare, alternatePushCheckout)).Success, "second push identity checkout");
            //CSJ,variable=identityPushRepository,識別情報プッシュリポジトリ
            var identityPushRepository = new GitRepository(originalPushCheckout);
            //CSJ,variable=oldPushTarget,旧プッシュ対象
            var oldPushTarget = (await identityPushRepository.InspectRemoteAsync(GitRemoteOperation.Push)).PushTarget
                ?? throw new InvalidOperationException("missing approved push identity");
            Directory.Move(originalPushCheckout, retainedPushCheckout);
            Directory.Move(alternatePushCheckout, originalPushCheckout);
            //CSJ,variable=currentPushTarget,現在のプッシュ対象
            var currentPushTarget = (await identityPushRepository.InspectRemoteAsync(GitRemoteOperation.Push)).PushTarget
                ?? throw new InvalidOperationException("missing replacement push identity");
            Check(currentPushTarget.RepositoryRoot == oldPushTarget.RepositoryRoot && currentPushTarget.Endpoint == oldPushTarget.Endpoint &&
                currentPushTarget.BranchRef == oldPushTarget.BranchRef && currentPushTarget.ObjectId == oldPushTarget.ObjectId,
                "replacement checkout differed in visible approval values");
            Check((await identityPushRepository.PushTrustedAsync(oldPushTarget)).Failure == GitFailure.UnsafeOperation,
                "stale push approval reused against a replacement repository");
            Check((await identityPushRepository.PushTrustedAsync(currentPushTarget)).Success,
                "fresh approval for a stable replacement was rejected");

            //CSJ,variable=approvedBranch,承認済みブランチ
            const string approvedBranch = "approval-fixture";
            //CSJ,variable=approvedRef,承認済み参照
            var approvedRef = "refs/heads/" + approvedBranch;
            await Command(clone, "switch", "-c", approvedBranch);
            Check((await two.PushTrustedAsync(approval)).Failure == GitFailure.UnsafeOperation, "push silently switched branch after approval");
            approval = (await two.InspectRemoteAsync(GitRemoteOperation.Push)).PushTarget!;
            await Command(clone, "commit", "--allow-empty", "-m", "advance approved branch");
            Check((await two.PushTrustedAsync(approval)).Failure == GitFailure.UnsafeOperation, "push silently advanced the approved commit");
            approval = (await two.InspectRemoteAsync(GitRemoteOperation.Push)).PushTarget!;

            two.BeforeRemoteExecution = _ =>
            {
                // HEAD moves after the operation's first validation without changing the approved commit.
                File.WriteAllText(Path.Combine(clone, ".git", "HEAD"), $"ref: refs/heads/{branch}\n");
            };
            Check((await two.PushTrustedAsync(approval)).Failure == GitFailure.UnsafeOperation, "push followed a branch change at launch");
            two.BeforeRemoteExecution = null;
            Check((await Command(root, "--git-dir", bare, "for-each-ref", "--format=%(refname)", approvedRef)).Length == 0,
                "stale approval updated a remote branch");
            await Command(clone, "switch", approvedBranch);
            Check((await two.PushTrustedAsync(approval)).Success, "push rejected the exact approved target");
            Check(await Command(root, "--git-dir", bare, "rev-parse", approvedRef) == approval.ObjectId,
                "push did not publish the approved commit");
            Check(await Command(root, "--git-dir", bare, "rev-parse", "refs/heads/" + branch) == remoteBeforeRejectedPush,
                "push changed an unapproved remote branch");
            //CSJ,variable=trustedClonePath,信頼済みクローンパス
            var trustedClonePath = Path.Combine(root, "trusted-clone");
            Check((await GitRepository.CloneTrustedAsync(bare, trustedClonePath)).Success && Directory.Exists(trustedClonePath), "trusted clone");
            //CSJ,variable=nestedRoot,入れ子ルート
            var nestedRoot = Path.Combine(source, "nested"); Directory.CreateDirectory(nestedRoot);
            Check((await new GitRepository(nestedRoot).FetchTrustedAsync(bare)).Failure == GitFailure.UnsafeOperation, "parent repository used by trust operation");
            await Command(source, "config", "--local", $"url.{decoy}.pushInsteadOf", bare);
            Check((await one.PushTrustedAsync(bare)).Failure == GitFailure.UnsafeOperation, "push used configured rewrite despite literal approval");
            await Command(source, "config", "--local", "--remove-section", $"url.{decoy}");
            const string explicitEndpoint = "https://approved.invalid/repository";
            await Command(source, "remote", "set-url", "origin", explicitEndpoint);
            var unsignedInspection = await one.InspectRemoteAsync(GitRemoteOperation.Fetch);
            Check(unsignedInspection.Success && unsignedInspection.StandardOutput == explicitEndpoint, "HTTPS preview was not literal");
            Check((await one.FetchAsync()).Failure == GitFailure.UnsafeOperation, "HTTPS fetch accepted without explicit trust");
            Check((await one.FetchTrustedAsync("https://other.invalid/repository")).Failure == GitFailure.UnsafeOperation, "trusted fetch accepted wrong HTTPS endpoint");
            //CSJ,variable=cloneInspection,クローン点検
            var cloneInspection = GitRepository.InspectCloneRemote(explicitEndpoint);
            Check(cloneInspection.Success && cloneInspection.StandardOutput == explicitEndpoint, "clone inspection failed to preserve the approved endpoint");
            Check(!GitRepository.InspectCloneRemote(explicitEndpoint + "=sample").Success,
                "endpoint cannot be represented in scoped Git configuration");
            Check(GitRepository.InspectCloneRemote(explicitEndpoint + "%3Dsample").Success,
                "percent-encoded path data was rejected despite safe configuration representation");
            Check(!GitRepository.InspectCloneRemote(explicitEndpoint + "\u200Bsample").Success,
                "endpoint confirmation accepted invisible format text");
            //CSJ,variable=credentialInspection,資格情報点検
            var credentialInspection = GitRepository.InspectCloneRemote("https://user:secret@approved.invalid/repository");
            Check(!credentialInspection.Success && !credentialInspection.StandardOutput.Contains("secret") &&
                !credentialInspection.StandardError.Contains("secret"), "clone inspection disclosed credentials");
            var unapprovedClone = Path.Combine(root, "unapproved-clone");
            Check((await GitRepository.CloneAsync(explicitEndpoint, unapprovedClone)).Failure == GitFailure.UnsafeOperation &&
                !Directory.Exists(unapprovedClone), "HTTPS clone accepted without explicit trust");
            await Command(source, "remote", "set-url", "origin", bare);
            //CSJ,variable=timeoutRepository,タイムアウトリポジトリ
            var timeoutRepository = new GitRepository(source) { RemoteTimeout = TimeSpan.FromSeconds(2) };
            var deadlineObserved = false;
            timeoutRepository.BeforeRemoteExecution = deadline => deadlineObserved = deadline.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            var timedOut = await timeoutRepository.FetchTrustedAsync(bare);
            Check(deadlineObserved && timedOut.Failure == GitFailure.Timeout && timedOut.RemoteDiagnostic is { Operation: "Fetch" }, "remote deadline was not operation-wide");
            try { await GitRepository.CloneAsync("https://user:secret@example.invalid/repo", Path.Combine(root, "unsafe")); throw new Exception("credentials accepted"); } catch (ArgumentException) { }
            try { await GitRepository.CloneAsync(bare, clone); throw new Exception("existing destination accepted"); } catch (IOException) { }
            //CSJ,variable=cancelled,キャンセル済み
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                //CSJ,variable=cancelledDestination,キャンセル済み格納先
                var cancelledDestination = Path.Combine(root, "cancelled");
                //CSJ,variable=countBefore,件数変更前
                var countBefore = Directory.EnumerateDirectories(root, ".ymmpm-clone-*").Count();
                try { await GitRepository.CloneAsync(bare, cancelledDestination, cancelled.Token); throw new Exception("cancel ignored"); } catch (OperationCanceledException) { }
                Check(!Directory.Exists(cancelledDestination) && countBefore == Directory.EnumerateDirectories(root, ".ymmpm-clone-*").Count(), "cancelled clone created files");
            }
            //CSJ,variable=ownedDestination,管理対象の格納先
            var ownedDestination = Path.Combine(root, "owned-clone");
            var existingStages = Directory.EnumerateDirectories(root, ".ymmpm-clone-*").ToHashSet(StringComparer.OrdinalIgnoreCase);
            string? observedStage = null;
            GitDirectoryIdentity? observedIdentity = null;
            //CSJ,variable=ownedClone,管理対象のクローン
            var ownedClone = await GitRepository.CloneCoreAsync(bare, ownedDestination, () =>
            {
                observedStage = Directory.EnumerateDirectories(root, ".ymmpm-clone-*").Single(path => !existingStages.Contains(path));
                using var witness = GitRepositoryLease.OpenDirectory(observedStage, FileShare.ReadWrite | FileShare.Delete);
                observedIdentity = GitRepositoryLease.ReadIdentity(witness);
                Check(observedIdentity.HasValue && File.Exists(Path.Combine(observedStage, ".git", "HEAD")),
                    "clone staging did not contain a complete repository");
            }, CancellationToken.None);
            Check(ownedClone.Success && observedIdentity.HasValue && !Directory.Exists(observedStage) &&
                existingStages.All(Directory.Exists), "exact clone failed or affected other staging paths");
            //CSJ,variable=published,確定済み
            using (var published = GitRepositoryLease.OpenDirectory(ownedDestination))
                Check(GitRepositoryLease.ReadIdentity(published) == observedIdentity,
                    "published directory was not the owned Git staging directory");
            Check(await Command(ownedDestination, "rev-parse", "HEAD") == remoteBeforeRejectedPush,
                "identity-preserving clone differs from the source branch");
            var raced = Path.Combine(root, "raced");
            var race = await GitRepository.CloneCoreAsync(bare, raced, () => { Directory.CreateDirectory(raced); File.WriteAllText(Path.Combine(raced, "sentinel"), "keep"); }, CancellationToken.None);
            Check(!race.Success && race.Failure == GitFailure.UnsafeOperation &&
                race.RecoveryPath is not null && File.Exists(Path.Combine(race.RecoveryPath, ".git", "HEAD")), "race clone recovery");
            Check(File.ReadAllText(Path.Combine(raced, "sentinel")) == "keep", "race overwrote destination");
            //CSJ,variable=publicationCancellation,確定反映キャンセル
            using (var publicationCancellation = new CancellationTokenSource())
            {
                //CSJ,variable=destination,格納先
                var destination = Path.Combine(root, "cancel-at-publication");
                try
                {
                    await GitRepository.CloneCoreAsync(bare, destination, publicationCancellation.Cancel, publicationCancellation.Token);
                    throw new InvalidOperationException("cancelled clone published its destination");
                }
                catch (OperationCanceledException exception)
                {
                    Check(!Directory.Exists(destination) && exception.Data["CloneRecoveryPath"] is string recovery && Directory.Exists(recovery),
                        "cancelled clone did not retain recoverable staging");
                }
            }
            var deep = root;
            while (deep.Length < 225) deep = Path.Combine(deep, "abcdefghij");
            if (deep.Length >= 248) Program.ReportConditionalSkip("long-path clone fixture parent exceeds Windows process working-directory limits");
            else
            {
                Directory.CreateDirectory(deep);
                var deepDestination = Path.Combine(deep, "clone");
                var deepResult = await GitRepository.CloneAsync(bare, deepDestination);
                if (deepResult.Success)
                {
                    Check(Directory.Exists(deepDestination), "long-path clone did not publish its destination");
                    Console.WriteLine("Long-path clone completed.");
                }
                else
                {
                    Check(deepResult.Failure == GitFailure.PathTooLong && !Directory.Exists(deepDestination) &&
                        deepResult.RecoveryPath is not null && Directory.Exists(deepResult.RecoveryPath),
                        $"unsafe or unclassified long-path failure ({deepResult.Failure}, exit {deepResult.ExitCode}): {deepResult.StandardError}");
                    Console.WriteLine("Git filesystem limit reached; owned staging retained and destination unpublished.");
                }
            }
            await GitConfirmedPullTests.RunAsync(workRoot);
            Console.WriteLine("Offline Git remote integration checks passed.");
        }
        finally { Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", global); Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", system); }
    }
}
