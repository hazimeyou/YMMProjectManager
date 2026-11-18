using System.Diagnostics;
using System.IO;

namespace YMMProjectManager.Infrastructure.Git;

/// <summary>Runs local Git operations without invoking a shell or changing the process working directory.</summary>
public sealed class GitRepository
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private readonly string directory;

    public GitRepository(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.GetFullPath(directory);
        if (!Directory.Exists(this.directory))
            throw new DirectoryNotFoundException(this.directory);
    }

    public Task<GitResult> InitializeAsync(CancellationToken token = default) => RunAsync(["init"], token);
    public Task<GitResult> DetectAsync(CancellationToken token = default) => RunAsync(["rev-parse", "--show-toplevel"], token);
    public Task<GitResult> StatusAsync(CancellationToken token = default) => RunAsync(["status", "--porcelain=v1", "-z"], token);
    public Task<GitResult> HistoryAsync(CancellationToken token = default) => RunAsync(["log", "-n", "100", "--format=%H%x00%an%x00%aI%x00%s"], token);
    public Task<GitResult> BranchesAsync(CancellationToken token = default) => RunAsync(["branch", "--list", "--no-color"], token);
    public Task<GitResult> StageAsync(IReadOnlyCollection<string> paths, CancellationToken token = default) => RunPathsAsync(["add", "--"], paths, token);
    public Task<GitResult> UnstageAsync(IReadOnlyCollection<string> paths, CancellationToken token = default) => RunPathsAsync(["restore", "--staged", "--"], paths, token);
    public Task<GitResult> CommitAsync(string message, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return RunAsync(["commit", "-m", message], token);
    }
    public Task<GitResult> CreateBranchAsync(string name, CancellationToken token = default) => RunBranchAsync("branch", name, token);
    public Task<GitResult> SwitchBranchAsync(string name, CancellationToken token = default) => RunBranchAsync("switch", name, token);

    private Task<GitResult> RunBranchAsync(string command, string name, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.StartsWith('-') || name.Contains('\0')) throw new ArgumentException("Invalid branch name.", nameof(name));
        return RunAsync([command, name], token);
    }

    private Task<GitResult> RunPathsAsync(string[] prefix, IReadOnlyCollection<string> paths, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0) throw new ArgumentException("At least one path is required.", nameof(paths));
        var args = new List<string>(prefix);
        foreach (var path in paths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (Path.IsPathRooted(path) || path.Contains('\0')) throw new ArgumentException("Paths must be relative to the repository.", nameof(paths));
            var fullPath = Path.GetFullPath(path, directory);
            if (!fullPath.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Path is outside the repository.", nameof(paths));
            args.Add(":(literal)" + path.Replace('\\', '/'));
        }
        return RunAsync(args, token);
    }

    private async Task<GitResult> RunAsync(IReadOnlyCollection<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES" })
            start.Environment.Remove(name);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Git.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Timeout);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException("Git operation timed out.");
        }
        return new GitResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}

public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}

