using System.Diagnostics;
using System.IO;
using YMMProjectManager.Infrastructure.Git;

internal static class GitRepositoryTests
{
    public static async Task RunAsync(string workRoot)
    {
        var root = Path.Combine(workRoot, "git-core");
        Directory.CreateDirectory(root);
        var git = new GitRepository(root);
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        Check((await git.InitializeAsync()).Success, "git init");
        foreach (var (key, value) in new[] { ("user.name", "Synthetic Tester"), ("user.email", "synthetic@example.invalid") })
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "config", "--local", key, value }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; await process.WaitForExitAsync(); Check(process.ExitCode == 0, "git config");
        }
        var marker = Path.Combine(root, "fsmonitor-ran");
        var monitor = Path.Combine(root, "monitor.sh");
        await File.WriteAllTextAsync(monitor, "#!/bin/sh\nprintf invoked > fsmonitor-ran\n");
        var config = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "config", "--local", "core.fsmonitor", monitor.Replace('\\', '/') }) config.ArgumentList.Add(arg);
        using (var process = Process.Start(config)!) { await process.WaitForExitAsync(); Check(process.ExitCode == 0, "fsmonitor config"); }
        await git.StatusAsync();
        Check(!File.Exists(marker), "untrusted fsmonitor was executed");
        await File.WriteAllTextAsync(Path.Combine(root, "one.txt"), "original");
        await File.WriteAllTextAsync(Path.Combine(root, "-special.txt"), "literal path");
        Check((await git.StageAsync(["one.txt", "-special.txt"])).Success, "stage paths");
        Check((await git.CommitAsync("first commit")).Success, "commit");
        var hooks = Path.Combine(root, "injected-hooks"); Directory.CreateDirectory(hooks);
        await File.WriteAllTextAsync(Path.Combine(hooks, "pre-commit"), "#!/bin/sh\nprintf invoked > injected-hook-ran\n");
        var previousCount = Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT");
        var previousKey = Environment.GetEnvironmentVariable("GIT_CONFIG_KEY_0");
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
            var probe = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "config", "--get", "core.hooksPath" }) probe.ArgumentList.Add(arg);
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
        var hookConfig = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "config", "--local", "core.hooksPath", hooks.Replace('\\', '/') }) hookConfig.ArgumentList.Add(arg);
        using (var process = Process.Start(hookConfig)!) { await process.WaitForExitAsync(); Check(process.ExitCode == 0, "fixture local hook config"); }
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
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await git.StatusAsync(cancelled.Token); throw new Exception("cancel ignored"); } catch (OperationCanceledException) { }
        Console.WriteLine("Offline Git local operations passed.");
        await Program.RunNamedCaseAsync("Reviewed Commit preserves staged and unstaged changes across a ref compare-and-swap", () => VerifyReviewedCommitAsync(workRoot));
        await Program.RunNamedCaseAsync("Reviewed Commit rejects changed staging, branch moves and cancellation", () => VerifyCommitInterferenceAsync(workRoot));
    }

    private static async Task VerifyReviewedCommitAsync(string workRoot)
    {
        var root = await MakeCommitFixtureAsync(workRoot, "commit-review");
        var git = new GitRepository(root);
        var changed = Path.Combine(root, "scène.txt");
        await File.WriteAllTextAsync(changed, "staged text");
        await File.WriteAllTextAsync(Path.Combine(root, "keep.txt"), "want to keep local");
        Require((await git.StageAsync(["scène.txt", "keep.txt"])).Success, "stage reviewed paths");
        Require((await git.UnstageAsync(["keep.txt"])).Success, "unstage independent path");
        var plan = (await git.InspectCommitAsync()).CommitTarget;
        Require(plan is not null && plan.Paths.SequenceEqual(["scène.txt"]), "exact staged path list");
        var index = await File.ReadAllBytesAsync(Path.Combine(root, ".git", "index"));
        await File.WriteAllTextAsync(changed, "later unstaged text");
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
        var root = await MakeCommitFixtureAsync(workRoot, "commit-interference");
        var git = new GitRepository(root);
        var head = (await CommandAsync(root, "rev-parse", "HEAD")).Trim();
        var file = Path.Combine(root, "scène.txt");
        await File.WriteAllTextAsync(file, "approved");
        Require((await git.StageAsync(["scène.txt"])).Success, "stage initial review");
        var old = (await git.InspectCommitAsync()).CommitTarget!;
        await File.WriteAllTextAsync(file, "different");
        Require((await git.StageAsync(["scène.txt"])).Success, "change index after review");
        Require(!(await git.CommitAsync(old, "must refuse stale review")).Success, "changed staged tree accepted");

        var current = (await git.InspectCommitAsync()).CommitTarget!;
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
        var rejected = await raced.CommitAsync(current, "must not overwrite");
        Require(rejected.Failure == GitFailure.ConcurrentOperation, "branch CAS accepted competing update");
        Require((await CommandAsync(root, "rev-parse", "HEAD")).Trim() == commit, "concurrent branch update preserved");
        Require((await CommandAsync(root, "symbolic-ref", "HEAD")).Trim() == current.BranchRef, "HEAD pin was ineffective");
    }

    private static async Task<string> MakeCommitFixtureAsync(string workRoot, string name)
    {
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

    private static Task<string> CommandAsync(string root, params string[] args) => CommandAsync(root, 0, args);

    private static async Task<string> CommandAsync(string root, int allowedExitCode, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        start.Environment["GIT_ALLOW_PROTOCOL"] = "";
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync();
        Require(child.ExitCode == allowedExitCode, $"fixture {args[0]} returned {child.ExitCode}: {await error}");
        return await output;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
