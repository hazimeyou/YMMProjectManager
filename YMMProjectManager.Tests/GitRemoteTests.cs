using System.Diagnostics;
using System.IO;
using YMMProjectManager.Infrastructure.Git;

internal static class GitRemoteTests
{
    public static async Task RunAsync(string workRoot)
    {
        var root = Path.Combine(workRoot, "git-remote"); Directory.CreateDirectory(root);
        var global = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        var system = Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", Path.Combine(root, "empty-global"));
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        try
        {
            static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
            async Task<string> Command(string cwd, params string[] args)
            {
                var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in args) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(); var stdout = await output; var stderr = await error;
                Check(process.ExitCode == 0, "fixture git: " + stderr); return stdout.Trim();
            }
            var source = Path.Combine(root, "source"); var bare = Path.Combine(root, "remote.git"); var clone = Path.Combine(root, "clone");
            Directory.CreateDirectory(source); var one = new GitRepository(source); Check((await one.InitializeAsync()).Success, "init");
            Check(!(await one.FetchAsync()).Success, "missing remote accepted");
            await Command(source, "config", "user.name", "Synthetic Tester"); await Command(source, "config", "user.email", "synthetic@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(source, "one.txt"), "one");
            Check((await one.StageAsync(["one.txt"])).Success && (await one.CommitAsync("first")).Success, "initial commit");
            await Command(root, "init", "--bare", bare); await Command(source, "remote", "add", "origin", bare);
            var branch = await Command(source, "symbolic-ref", "--short", "HEAD");
            var expectedCommit = await Command(source, "rev-parse", "HEAD");
            await Command(root, "--git-dir", bare, "symbolic-ref", "HEAD", "refs/heads/" + branch);
            var decoy = Path.Combine(root, "decoy.git");
            await Command(root, "init", "--bare", decoy);
            one.BeforeRemoteExecution = () =>
            {
                var start = new ProcessStartInfo("git") { WorkingDirectory = source, UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { "remote", "set-url", "origin", decoy }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!; process.WaitForExit();
                Check(process.ExitCode == 0, "change remote fixture");
                var switchHead = new ProcessStartInfo("git") { WorkingDirectory = source, UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { "symbolic-ref", "HEAD", "refs/heads/concurrent-unborn" }) switchHead.ArgumentList.Add(arg);
                using var headProcess = Process.Start(switchHead)!; headProcess.WaitForExit();
                Check(headProcess.ExitCode == 0, "change HEAD fixture");
            };
            Check((await one.PushAsync()).Success, "initial push");
            one.BeforeRemoteExecution = null;
            Check(await Command(root, "--git-dir", bare, "rev-parse", "refs/heads/" + branch) == expectedCommit, "push used changed HEAD");
            await Command(source, "symbolic-ref", "HEAD", "refs/heads/" + branch);
            Check(!Directory.EnumerateFiles(Path.Combine(decoy, "refs", "heads"), "*", SearchOption.AllDirectories).Any(), "push followed changed origin");
            await Command(source, "remote", "set-url", "origin", bare);
            Check((await GitRepository.CloneAsync(bare, clone)).Success, "clone"); var two = new GitRepository(clone);
            await Command(clone, "config", "user.name", "Synthetic Tester"); await Command(clone, "config", "user.email", "synthetic@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(clone, "two.txt"), "two");
            Check((await two.StageAsync(["two.txt"])).Success && (await two.CommitAsync("second")).Success && (await two.PushAsync()).Success, "clone push");
            await Command(source, "branch", "race-side");
            var sideBefore = await Command(source, "rev-parse", "refs/heads/race-side");
            one.BeforeRemoteExecution = () =>
            {
                var start = new ProcessStartInfo("git") { WorkingDirectory = source, UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("switch"); start.ArgumentList.Add("race-side");
                using var process = Process.Start(start)!; process.WaitForExit();
                Check(process.ExitCode == 0, "switch branch during pull fixture");
            };
            var racedPull = await one.PullAsync();
            one.BeforeRemoteExecution = null;
            Check(!racedPull.Success && racedPull.Failure == GitFailure.UnsafeOperation && !string.IsNullOrWhiteSpace(racedPull.StandardError), "raced pull did not report failure");
            Check(await Command(source, "symbolic-ref", "--short", "HEAD") == "race-side", "pull race fixture did not switch branches");
            Check(await Command(source, "rev-parse", "refs/heads/race-side") == sideBefore, "raced pull changed unintended branch");
            await Command(source, "switch", branch);
            using (var cancelledPull = new CancellationTokenSource())
            {
                cancelledPull.Cancel();
                try { await one.PullAsync(cancelledPull.Token); throw new Exception("pull cancellation ignored"); }
                catch (OperationCanceledException) { }
            }
            Check((await one.FetchAsync()).Success, "fetch");
            var disabledPull = await one.PullAsync();
            Check(!disabledPull.Success && disabledPull.Failure == GitFailure.UnsafeOperation, "pull must fail closed");
            Program.ReportConditionalSkip("product fast-forward pull disabled pending cross-process branch protection");
            await Command(source, "merge", "--ff-only", "refs/remotes/origin/" + branch);
            Check(await Command(source, "rev-parse", "HEAD") == await Command(clone, "rev-parse", "HEAD"), "heads differ");
            await File.WriteAllTextAsync(Path.Combine(source, "local.txt"), "local");
            Check((await one.StageAsync(["local.txt"])).Success && (await one.CommitAsync("local")).Success, "local commit");
            await File.WriteAllTextAsync(Path.Combine(clone, "remote.txt"), "remote");
            Check((await two.StageAsync(["remote.txt"])).Success && (await two.CommitAsync("remote")).Success && (await two.PushAsync()).Success, "remote commit");
            var prior = await Command(source, "rev-parse", "HEAD");
            var rejectedPush = await one.PushAsync(); var rejectedPull = await one.PullAsync();
            Check(!rejectedPush.Success && !rejectedPull.Success, "divergence accepted");
            Check(rejectedPush.Failure == GitFailure.Divergence && rejectedPull.Failure == GitFailure.UnsafeOperation, "failure classification");
            Program.ReportConditionalSkip("product pull divergence classification disabled pending safe pull implementation");
            Check(await Command(source, "rev-parse", "HEAD") == prior, "divergence changed local head");
            await Command(source, "remote", "set-url", "origin", "https://user:secret@example.invalid/repo");
            var unsafeOrigin = await one.OriginAsync();
            Check(!unsafeOrigin.Success && !unsafeOrigin.StandardOutput.Contains("secret") && !unsafeOrigin.StandardError.Contains("secret"), "credential disclosure");
            await Command(source, "remote", "set-url", "origin", "https://alpha.invalid/repo");
            await Command(source, "config", "--local", "url.https://beta.invalid/.insteadOf", "https://alpha.invalid/");
            await Command(source, "config", "--local", "url.https://gamma.invalid/.insteadOf", "https://beta.invalid/");
            var once = await Command(source, "remote", "get-url", "origin");
            var twice = await Command(source, "ls-remote", "--get-url", once);
            Check(once == "https://beta.invalid/repo" && twice == "https://gamma.invalid/repo", "rewrite fixture changed");
            await Command(source, "remote", "set-url", "origin", bare);
            try { await GitRepository.CloneAsync("https://user:secret@example.invalid/repo", Path.Combine(root, "unsafe")); throw new Exception("credentials accepted"); } catch (ArgumentException) { }
            try { await GitRepository.CloneAsync(bare, clone); throw new Exception("existing destination accepted"); } catch (IOException) { }
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var cancelledDestination = Path.Combine(root, "cancelled");
                var countBefore = Directory.EnumerateDirectories(root, ".ymmpm-clone-*").Count();
                try { await GitRepository.CloneAsync(bare, cancelledDestination, cancelled.Token); throw new Exception("cancel ignored"); } catch (OperationCanceledException) { }
                Check(!Directory.Exists(cancelledDestination) && countBefore == Directory.EnumerateDirectories(root, ".ymmpm-clone-*").Count(), "cancelled clone created files");
            }
            var raced = Path.Combine(root, "raced");
            var race = await GitRepository.CloneCoreAsync(bare, raced, () => { Directory.CreateDirectory(raced); File.WriteAllText(Path.Combine(raced, "sentinel"), "keep"); }, CancellationToken.None);
            Check(!race.Success && race.RecoveryPath is not null && Directory.Exists(race.RecoveryPath), "race clone recovery");
            Check(File.ReadAllText(Path.Combine(raced, "sentinel")) == "keep", "race overwrote destination");
            var deep = root;
            while (deep.Length < 225) deep = Path.Combine(deep, "abcdefghij");
            if (deep.Length >= 248) Program.ReportConditionalSkip("long-path clone fixture parent exceeds Windows process working-directory limits");
            else
            {
                Directory.CreateDirectory(deep);
                var deepDestination = Path.Combine(deep, "clone");
                var deepResult = await GitRepository.CloneAsync(bare, deepDestination);
                Check(deepResult.Success || (deepResult.RecoveryPath is not null && Directory.Exists(deepResult.RecoveryPath)), "long-path reservation failed without a recoverable owned directory");
                Console.WriteLine(deepResult.Success ? "Long-path clone completed." : "Long-path clone failed with an owned recovery directory; Git filesystem limits remain.");
            }
            Console.WriteLine("Offline Git remote integration checks passed.");
        }
        finally { Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", global); Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", system); }
    }
}
