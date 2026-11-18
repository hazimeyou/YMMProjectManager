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
            await Command(root, "--git-dir", bare, "symbolic-ref", "HEAD", "refs/heads/" + branch);
            Check((await one.PushAsync()).Success, "initial push");
            Check((await GitRepository.CloneAsync(bare, clone)).Success, "clone"); var two = new GitRepository(clone);
            await Command(clone, "config", "user.name", "Synthetic Tester"); await Command(clone, "config", "user.email", "synthetic@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(clone, "two.txt"), "two");
            Check((await two.StageAsync(["two.txt"])).Success && (await two.CommitAsync("second")).Success && (await two.PushAsync()).Success, "clone push");
            Check((await one.FetchAsync()).Success && (await one.PullAsync()).Success, "fetch and fast-forward pull");
            Check(await Command(source, "rev-parse", "HEAD") == await Command(clone, "rev-parse", "HEAD"), "heads differ");
            await File.WriteAllTextAsync(Path.Combine(source, "local.txt"), "local");
            Check((await one.StageAsync(["local.txt"])).Success && (await one.CommitAsync("local")).Success, "local commit");
            await File.WriteAllTextAsync(Path.Combine(clone, "remote.txt"), "remote");
            Check((await two.StageAsync(["remote.txt"])).Success && (await two.CommitAsync("remote")).Success && (await two.PushAsync()).Success, "remote commit");
            var prior = await Command(source, "rev-parse", "HEAD");
            var rejectedPush = await one.PushAsync(); var rejectedPull = await one.PullAsync();
            Check(!rejectedPush.Success && !rejectedPull.Success, "divergence accepted");
            Check(rejectedPush.Failure == GitFailure.Divergence && rejectedPull.Failure == GitFailure.Divergence, "divergence classification");
            Check(await Command(source, "rev-parse", "HEAD") == prior, "divergence changed local head");
            await Command(source, "remote", "set-url", "origin", "https://user:secret@example.invalid/repo");
            var unsafeOrigin = await one.OriginAsync();
            Check(!unsafeOrigin.Success && !unsafeOrigin.StandardOutput.Contains("secret") && !unsafeOrigin.StandardError.Contains("secret"), "credential disclosure");
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
            Console.WriteLine("Offline Git remote integration checks passed.");
        }
        finally { Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", global); Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", system); }
    }
}
