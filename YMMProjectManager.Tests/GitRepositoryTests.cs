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
        Check(File.Exists(Path.Combine(root, "injected-hook-ran")), "intentionally configured repository hook was suppressed");
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
    }
}
