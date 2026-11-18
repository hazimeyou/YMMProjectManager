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
