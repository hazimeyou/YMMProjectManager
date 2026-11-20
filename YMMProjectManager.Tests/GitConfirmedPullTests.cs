using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using YMMProjectManager.Infrastructure.Git;

internal static class GitConfirmedPullTests
{
    public static async Task RunAsync(string workRoot)
    {
        await Program.RunNamedCaseAsync("Confirmed Pull fast-forwards the approved ref, index and Unicode worktree", () => FastForwardAsync(workRoot));
        await Program.RunNamedCaseAsync("Confirmed Pull preserves dirty, staged, untracked and ignored data", () => ProtectedFilesAsync(workRoot));
        await Program.RunNamedCaseAsync("Confirmed Pull refuses divergence and a missing configured upstream", () => DivergenceAsync(workRoot));
        await Program.RunNamedCaseAsync("Confirmed Pull rejects an upstream deleted or moved after approval", () => RemoteChangedAsync(workRoot));
        await Program.RunNamedCaseAsync("Confirmed Pull detects an external branch switch before Git starts", () => BranchSwitchAsync(workRoot));
        await Program.RunNamedCaseAsync("Confirmed Pull refuses concurrent app writes", () => ConcurrentWritesAsync(workRoot));
        await Program.RunNamedCaseAsync("Confirmed Pull honors cancellation before launch and verifies after launch", () => CancelAsync(workRoot));
        await Program.RunNamedCaseAsync("Confirmed Pull retains ambiguous evidence across repository instances", () => RecoveryAsync(workRoot));
    }

    private const string Incoming = "素材.txt";
    private sealed record Fixture(string Local, string Bare, string Start, string Target, GitRepository Repository, string FixtureRoot);

    private static async Task<Fixture> CreateAsync(string workRoot, string caseName)
    {
        var root = Path.Combine(workRoot, "cp", caseName);
        var seed = Path.Combine(root, "s");
        var bare = Path.Combine(root, "r");
        var local = Path.Combine(root, "l");
        Directory.CreateDirectory(seed);
        await GitAsync(seed, "init", "-b", "main");
        await GitAsync(seed, "config", "user.name", "Synthetic Pull Tester");
        await GitAsync(seed, "config", "user.email", "synthetic@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(seed, "existing.txt"), "base");
        await GitAsync(seed, "add", "existing.txt");
        await GitAsync(seed, "commit", "-m", "base");
        var start = await GitAsync(seed, "rev-parse", "HEAD");
        await GitAsync(root, "clone", "--bare", seed, bare);
        await GitAsync(root, "clone", bare, local);
        await GitAsync(seed, "remote", "add", "origin", bare);
        await File.WriteAllTextAsync(Path.Combine(seed, Incoming), "incoming Unicode content");
        await File.WriteAllTextAsync(Path.Combine(seed, "existing.txt"), "upstream version");
        await GitAsync(seed, "add", "--", "existing.txt", Incoming);
        await GitAsync(seed, "commit", "-m", "target");
        var target = await GitAsync(seed, "rev-parse", "HEAD");
        await GitAsync(seed, "push", "origin", "main");
        return new Fixture(local, bare, start, target, new GitRepository(local), root);
    }

    private static async Task<GitPullTarget> ApproveAsync(Fixture fixture)
    {
        var remote = await fixture.Repository.InspectPullRemoteAsync();
        Check(remote.Success && remote.PullRemote is { BranchRef: "refs/heads/main", RemoteName: "origin", UpstreamRef: "refs/heads/main" },
            "local approval inspection: " + remote.StandardError);
        var target = await fixture.Repository.InspectPullTargetAsync(remote.PullRemote!);
        Check(target.Success && target.PullTarget is not null, "remote approval inspection: " + target.StandardError);
        return target.PullTarget!;
    }

    private static async Task FastForwardAsync(string workRoot)
    {
        var fixture = await CreateAsync(workRoot, "ff");
        await GitAsync(fixture.Local, "config", "merge.autostash", "true");
        var target = await ApproveAsync(fixture);
        Check(target.ChangesWorktree && target.StartObjectId == fixture.Start && target.TargetObjectId == fixture.Target, "approval was stale");
        Check((await RunGitAsync(fixture.Local, "cat-file", "-e", fixture.Target)).ExitCode != 0, "preview imported target objects");
        Check((await fixture.Repository.PullAsync()).Failure == GitFailure.UnsafeOperation, "unconfirmed Pull accepted");
        Check(await GitAsync(fixture.Local, "rev-parse", "HEAD") == fixture.Start, "preview changed HEAD");

        var result = await fixture.Repository.PullTrustedAsync(target);
        Check(result.Success, $"fast-forward failed: {result.Failure} {result.StandardError}");
        await VerifyUpdatedAsync(fixture);
        Check(!GitPullOperationJournal.Exists(fixture.Local), "verified update retained a pending operation");
        Check(!File.Exists(Path.Combine(fixture.Local, ".git", "FETCH_HEAD")), "object import wrote FETCH_HEAD");

        var noChange = await ApproveAsync(fixture);
        Check(!noChange.ChangesWorktree && (await fixture.Repository.PullTrustedAsync(noChange)).Success, "up-to-date workflow");
        await VerifyUpdatedAsync(fixture);
    }

    private static async Task VerifyUpdatedAsync(Fixture fixture)
    {
        Check(await GitAsync(fixture.Local, "symbolic-ref", "HEAD") == "refs/heads/main" &&
            await GitAsync(fixture.Local, "rev-parse", "refs/heads/main") == fixture.Target, "ref and selected branch disagreed");
        Check(await GitAsync(fixture.Local, "write-tree") == await GitAsync(fixture.Local, "rev-parse", "HEAD^{tree}"), "index tree differed from HEAD");
        Check(await GitAsync(fixture.Local, "status", "--porcelain") == "" &&
            await File.ReadAllTextAsync(Path.Combine(fixture.Local, Incoming)) == "incoming Unicode content" &&
            await File.ReadAllTextAsync(Path.Combine(fixture.Local, "existing.txt")) == "upstream version", "unexpected checkout state");
    }

    private static async Task ProtectedFilesAsync(string workRoot)
    {
        var fixture = await CreateAsync(workRoot, "dirty");
        var target = await ApproveAsync(fixture);
        var existing = Path.Combine(fixture.Local, "existing.txt");
        var incoming = Path.Combine(fixture.Local, Incoming);
        var index = Path.Combine(fixture.Local, ".git", "index");
        await GitAsync(fixture.Local, "config", "merge.autostash", "true");
        foreach (var state in new[] { "dirty", "staged", "untracked", "ignored" })
        {
            var changed = state is "dirty" or "staged" ? existing : incoming;
            if (state == "ignored")
                await File.AppendAllTextAsync(Path.Combine(fixture.Local, ".git", "info", "exclude"), "\n/" + Incoming + "\n");
            await File.WriteAllTextAsync(changed, "preserve " + state);
            if (state == "staged") await GitAsync(fixture.Local, "add", "--", "existing.txt");
            var previousIndex = SHA256.HashData(await File.ReadAllBytesAsync(index));
            var result = await fixture.Repository.PullTrustedAsync(target);
            Check(!result.Success && result.Failure != GitFailure.OutcomeUnknown, $"protected file accepted: {state} {result.StandardError}");
            Check(await GitAsync(fixture.Local, "rev-parse", "HEAD") == fixture.Start &&
                await File.ReadAllTextAsync(changed) == "preserve " + state &&
                SHA256.HashData(await File.ReadAllBytesAsync(index)).SequenceEqual(previousIndex), "protected state changed: " + state);
            if (changed == existing)
            {
                await File.WriteAllTextAsync(existing, "base");
                await GitAsync(fixture.Local, "add", "--", "existing.txt");
            }
            else File.Delete(changed);
        }

        var indexLock = index + ".lock";
        await File.WriteAllTextAsync(indexLock, "external holder");
        Check(!(await fixture.Repository.PullTrustedAsync(target)).Success &&
            await File.ReadAllTextAsync(indexLock) == "external holder", "external index lock was consumed");
    }

    private static async Task DivergenceAsync(string workRoot)
    {
        var fixture = await CreateAsync(workRoot, "diverge");
        await GitAsync(fixture.Local, "config", "user.name", "Synthetic Pull Tester");
        await GitAsync(fixture.Local, "config", "user.email", "synthetic@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(fixture.Local, "own.txt"), "owned");
        await GitAsync(fixture.Local, "add", "own.txt");
        await GitAsync(fixture.Local, "commit", "-m", "local commit");
        var local = await GitAsync(fixture.Local, "rev-parse", "HEAD");
        var target = await ApproveAsync(fixture);
        Check((await fixture.Repository.PullTrustedAsync(target)).Failure == GitFailure.Divergence, "divergence was not classified");
        Check(await GitAsync(fixture.Local, "rev-parse", "HEAD") == local && await GitAsync(fixture.Local, "status", "--porcelain") == "",
            "divergence modified the checkout");
        await GitAsync(fixture.Local, "config", "--unset", "branch.main.merge");
        var missing = await fixture.Repository.InspectPullRemoteAsync();
        Check(!missing.Success && missing.PullRemote is null, "an unconfigured upstream was inferred");
    }

    private static async Task RemoteChangedAsync(string workRoot)
    {
        var fixture = await CreateAsync(workRoot, "remote");
        var target = await ApproveAsync(fixture);
        await GitAsync(fixture.Bare, "update-ref", "-d", "refs/heads/main", fixture.Target);
        Check(!(await fixture.Repository.PullTrustedAsync(target)).Success, "deleted upstream was accepted");
        await GitAsync(fixture.Bare, "update-ref", "refs/heads/main", fixture.Target);
        fixture.Repository.BeforePullExecutionRecheck = _ =>
            GitAsync(fixture.Bare, "update-ref", "refs/heads/main", fixture.Start, fixture.Target).GetAwaiter().GetResult();
        Check(!(await fixture.Repository.PullTrustedAsync(target)).Success, "moved upstream was accepted after import");
        Check(await GitAsync(fixture.Local, "rev-parse", "HEAD") == fixture.Start &&
            !File.Exists(Path.Combine(fixture.Local, Incoming)) && !GitPullOperationJournal.Exists(fixture.Local), "remote change left checkout affected");
    }

    private static async Task BranchSwitchAsync(string workRoot)
    {
        var fixture = await CreateAsync(workRoot, "branch");
        await GitAsync(fixture.Local, "branch", "side");
        var target = await ApproveAsync(fixture);
        fixture.Repository.AfterPullJournalCreated = () => GitAsync(fixture.Local, "switch", "side").GetAwaiter().GetResult();
        var result = await fixture.Repository.PullTrustedAsync(target);
        Check(!result.Success && !GitPullOperationJournal.Exists(fixture.Local), "branch switch caused mutation or unresolved evidence");
        Check(await GitAsync(fixture.Local, "symbolic-ref", "HEAD") == "refs/heads/side" &&
            await GitAsync(fixture.Local, "rev-parse", "refs/heads/side") == fixture.Start &&
            await GitAsync(fixture.Local, "rev-parse", "refs/heads/main") == fixture.Start &&
            await GitAsync(fixture.Local, "status", "--porcelain") == "", "a branch was unexpectedly advanced");
    }

    private static async Task ConcurrentWritesAsync(string workRoot)
    {
        var fixture = await CreateAsync(workRoot, "gate");
        var target = await ApproveAsync(fixture);
        var another = new GitRepository(fixture.Local);
        var asserted = false;
        fixture.Repository.BeforePullExecutionRecheck = _ =>
        {
            Check(another.PullTrustedAsync(target).GetAwaiter().GetResult().Failure == GitFailure.ConcurrentOperation,
                "simultaneous Pull was admitted");
            Check(another.CreateBranchAsync("r19-blocked").GetAwaiter().GetResult().Failure == GitFailure.ConcurrentOperation,
                "branch creation bypassed the write gate");
            Check(another.FetchAsync().GetAwaiter().GetResult().Failure == GitFailure.ConcurrentOperation,
                "Fetch bypassed the write gate");
            asserted = true;
        };
        var result = await fixture.Repository.PullTrustedAsync(target);
        Check(asserted && result.Success, "the approved write did not complete: " + result.StandardError);
        Check((await another.CreateBranchAsync("r19-allowed")).Success, "write gate was not released");
    }

    private static async Task CancelAsync(string workRoot)
    {
        var fixture = await CreateAsync(workRoot, "cancel");
        var target = await ApproveAsync(fixture);
        using (var cancellation = new CancellationTokenSource())
        {
            fixture.Repository.AfterPullJournalCreated = cancellation.Cancel;
            try
            {
                await fixture.Repository.PullTrustedAsync(target, cancellation.Token);
                throw new InvalidOperationException("pre-launch cancellation was ignored");
            }
            catch (OperationCanceledException) { }
        }
        Check(await GitAsync(fixture.Local, "rev-parse", "HEAD") == fixture.Start &&
            !File.Exists(Path.Combine(fixture.Local, Incoming)) && !GitPullOperationJournal.Exists(fixture.Local),
            "pre-launch cancellation changed the checkout");
        fixture.Repository.AfterPullJournalCreated = null;

        using var duringExecution = new CancellationTokenSource();
        fixture.Repository.AfterPullProcessStarted = duringExecution.Cancel;
        var result = await fixture.Repository.PullTrustedAsync(target, duringExecution.Token);
        Check(duringExecution.IsCancellationRequested && result.Success, "executing update was not verified after cancellation: " + result.StandardError);
        await VerifyUpdatedAsync(fixture);
    }

    private static async Task RecoveryAsync(string workRoot)
    {
        var fixture = await CreateAsync(workRoot, "unknown");
        var target = await ApproveAsync(fixture);
        fixture.Repository.AfterPullProcessExit = () => File.WriteAllText(Path.Combine(fixture.Local, Incoming), "external concurrent edits");
        var result = await fixture.Repository.PullTrustedAsync(target);
        Check(result.Failure == GitFailure.OutcomeUnknown && result.RecoveryPath is { } evidence && File.Exists(evidence),
            "concurrent edit allowed success or lost the durable record: " + result.StandardError);
        var restarted = new GitRepository(fixture.Local);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Check((await restarted.InspectPullRemoteAsync()).Failure == GitFailure.OutcomeUnknown &&
                (await restarted.PullTrustedAsync(target)).Failure == GitFailure.OutcomeUnknown, "new service retried an unverifiable result");
        }
        Check(await File.ReadAllTextAsync(Path.Combine(fixture.Local, Incoming)) == "external concurrent edits" &&
            await GitAsync(fixture.Local, "rev-parse", "HEAD") == fixture.Target, "automatic recovery reverted external edits or checkout");
    }

    private static async Task<string> GitAsync(string directory, params string[] arguments)
    {
        var result = await RunGitAsync(directory, arguments);
        Check(result.ExitCode == 0, "synthetic Git command " + string.Join(" ", arguments) + ": " + result.Error);
        return result.Output.Trim();
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunGitAsync(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start synthetic Git.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }

    private static void Check(bool condition, string diagnostic)
    {
        if (!condition) throw new InvalidOperationException(diagnostic);
    }
}
