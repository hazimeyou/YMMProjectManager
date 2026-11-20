using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Git;

internal static class GitPullRecoveryTests
{
    internal const string ChildArgument = "--git-pull-recovery-child";
    private const string FixtureDirectory = "git-pull-recovery";
    private const int InterruptedExitCode = 86;
    private const string HeadText = "ref: refs/heads/test-main\n";
    private const string IndexText = "synthetic index bytes";
    private const string WorkingText = "working project data";
    private const string UnrelatedText = "unrelated recovery evidence";
    private static readonly GitPullOperationJournal.ApprovalEvidence Approved = new(
        "refs/heads/test-main", new string('1', 40), "origin", "refs/heads/test-main", new string('2', 40));

    internal static async Task RunAsync(string workRoot)
    {
        var root = Path.Combine(workRoot, FixtureDirectory);
        await Program.RunNamedCaseAsync("Pull recovery retains the mutation marker after child process termination",
            () => TestInterruptedChildAsync(CreateFixture(root, "interrupted")));
        await Program.RunNamedCaseAsync("Pull marker publishes at most one record and clears only the owned file",
            () => TestOwnedJournalAsync(CreateFixture(root, "owned")));
        await Program.RunNamedCaseAsync("Pull recovery preserves an unrecognized existing marker",
            () => TestUnrecognizedMarkerAsync(CreateFixture(root, "unrecognized")));
        await Program.RunNamedCaseAsync("App-owned Git writers cannot acquire a second lease",
            () => TestWriterLeaseAsync(CreateFixture(root, "writers")));
        await Program.RunNamedCaseAsync("Pull checks Windows tree names and modes before native checkout", () =>
        {
            static string FileEntry(string path, string mode = "100644") =>
                $"{mode} blob {new string('1', 40)}\t{path}\0";
            static string TreeEntry(string path) =>
                $"040000 tree {new string('2', 40)}\t{path}\0";
            Require(GitPullWindowsTree.CanApply(TreeEntry("素材") + FileEntry("素材/台本.ymmp", "100755")),
                "A normal Unicode tree cannot be checked out.");
            Require(!GitPullWindowsTree.CanApply(FileEntry("Cover.txt") + FileEntry("cover.txt")) &&
                !GitPullWindowsTree.CanApply(TreeEntry("Media") + TreeEntry("media")),
                "A case-distinct path collision was accepted.");
            Require(!GitPullWindowsTree.CanApply(FileEntry("media/COM¹.wav")) &&
                !GitPullWindowsTree.CanApply(TreeEntry(".Git")) &&
                !GitPullWindowsTree.CanApply(FileEntry("media/name.")) &&
                !GitPullWindowsTree.CanApply(FileEntry("media/name ", "120000")) &&
                !GitPullWindowsTree.CanApply(FileEntry("media/submodule", "160000")) &&
                !GitPullWindowsTree.CanApply(FileEntry("unterminated")[..^1]),
                "An unsupported Win32 path, mode or malformed tree was accepted.");
            return Task.CompletedTask;
        });
        await Program.RunNamedCaseAsync("Confirmed Pull refuses a target that contains case-colliding files",
            () => TestCaseCollidingTargetAsync(root));
        await Program.RunNamedCaseAsync("Pull rejects UNC endpoint syntax before any filesystem lookup", () =>
        {
            Require(Path.IsPathFullyQualified("//synthetic.invalid/share") &&
                !GitRepository.OnFixedLocalNtfs("//synthetic.invalid/share") &&
                !GitRepository.OnFixedLocalNtfs(@"\\synthetic.invalid\share\project.git"),
                "A UNC endpoint was classified as a fixed local drive.");
            return Task.CompletedTask;
        });
        await Program.RunNamedCaseAsync("Read-only Pull diagnostics decline mutable Git config includes", async () =>
        {
            var worktree = Directory.CreateDirectory(Path.Combine(root, "diagnostic")).FullName;
            await GitAsync(worktree, ["init", "-b", "main"]);
            await GitAsync(worktree, ["config", "user.name", "Synthetic Git Fixture"]);
            await GitAsync(worktree, ["config", "user.email", "fixture@example.invalid"]);
            await File.WriteAllTextAsync(Path.Combine(worktree, "project.ymmp"), "{}");
            await GitAsync(worktree, ["add", "--", "project.ymmp"]);
            await GitAsync(worktree, ["commit", "-m", "baseline"]);
            var repository = new GitRepository(worktree);
            Require((await repository.InspectPullAsync()).State == GitPullState.TrackingUnavailable,
                "The normal checkout could not be diagnosed.");
            var include = Path.Combine(root, "empty-config");
            await File.WriteAllTextAsync(include, "# empty synthetic include\n");
            await GitAsync(worktree, ["config", "include.path", include]);
            Require((await repository.InspectPullAsync()).State == GitPullState.IndexNotVerifiable,
                "Diagnostics admitted mutable include configuration.");
        });
    }

    private static async Task TestCaseCollidingTargetAsync(string root)
    {
        var seed = Directory.CreateDirectory(Path.Combine(root, "case-seed")).FullName;
        var remote = Path.Combine(root, "case-remote.git");
        var worktree = Path.Combine(root, "case-作業コピー");
        await GitAsync(seed, ["init", "-b", "main"]);
        await GitAsync(seed, ["config", "user.name", "Synthetic Git Fixture"]);
        await GitAsync(seed, ["config", "user.email", "fixture@example.invalid"]);
        await File.WriteAllTextAsync(Path.Combine(seed, "keep.ymmp"), "{}");
        await GitAsync(seed, ["add", "--", "keep.ymmp"]);
        await GitAsync(seed, ["commit", "-m", "baseline"]);
        var starting = await GitAsync(seed, ["rev-parse", "HEAD"]);
        await GitAsync(seed, ["init", "--bare", "-b", "main", remote]);
        await GitAsync(seed, ["push", "--", remote, "main"]);
        await GitAsync(seed, ["clone", "--", remote, worktree]);
        var indexPath = Path.Combine(worktree, ".git", "index");
        var index = await File.ReadAllBytesAsync(indexPath);
        var original = await File.ReadAllBytesAsync(Path.Combine(worktree, "keep.ymmp"));

        var first = await GitAsync(seed, ["hash-object", "-w", "--stdin"], "first");
        var second = await GitAsync(seed, ["hash-object", "-w", "--stdin"], "second");
        var originalBlob = await GitAsync(seed, ["rev-parse", "HEAD:keep.ymmp"]);
        var tree = await GitAsync(seed, ["mktree", "-z"],
            $"100644 blob {first}\tCover.txt\0" +
            $"100644 blob {second}\tcover.txt\0" +
            $"100644 blob {originalBlob}\tkeep.ymmp\0");
        var target = await GitAsync(seed, ["commit-tree", tree, "-p", starting], "case-distinct synthetic files");
        await GitAsync(seed, ["push", "--", remote, target + ":refs/heads/main"]);

        var service = new GitRepository(worktree);
        var remotePreview = await service.InspectPullRemoteAsync();
        Require(remotePreview.Success && remotePreview.PullRemote is not null, "Cannot bind the isolated upstream.");
        await AssertMetadataJunctionRejectedAsync(root, worktree, service);
        var preview = await service.InspectPullTargetAsync(remotePreview.PullRemote!);
        Require(preview.Success && preview.PullTarget?.TargetObjectId == target, "The approved commit was not observed.");
        var result = await service.PullTrustedAsync(preview.PullTarget!);
        Require(!result.Success && result.Failure == GitFailure.UnsafeOperation &&
            result.StandardError.Contains("paths or file types", StringComparison.Ordinal) &&
            !GitPullOperationJournal.Exists(worktree),
            $"The Windows tree guard did not reject the collision at preflight ({result.ExitCode}, {result.Failure}, {result.StandardError}).");
        var finalIndex = await File.ReadAllBytesAsync(indexPath);
        var finalOriginal = await File.ReadAllBytesAsync(Path.Combine(worktree, "keep.ymmp"));
        Require(await GitAsync(worktree, ["rev-parse", "HEAD"]) == starting &&
            await GitAsync(worktree, ["symbolic-ref", "HEAD"]) == "refs/heads/main" &&
            index.SequenceEqual(finalIndex) && original.SequenceEqual(finalOriginal) &&
            !File.Exists(Path.Combine(worktree, "Cover.txt")), "A rejected target changed the branch, index or worktree.");
    }

    private static async Task AssertMetadataJunctionRejectedAsync(string root, string worktree, GitRepository repository)
    {
        var target = Directory.CreateDirectory(Path.Combine(root, "junction-target")).FullName;
        var link = Path.Combine(worktree, ".git", "objects", "reserved-fixture");
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start junction fixture.");
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var error = process.StandardError.ReadToEndAsync(limit.Token);
        try { await process.WaitForExitAsync(limit.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        await output;
        var details = await error;
        Require(process.ExitCode == 0, $"Could not create a junction inside the synthetic checkout. {details}");
        try
        {
            var blocked = await repository.InspectPullRemoteAsync();
            Require(!blocked.Success && blocked.Failure == GitFailure.UnsafeOperation && blocked.PullRemote is null &&
                !Directory.EnumerateFileSystemEntries(target).Any(), "Pull offered approval for unsupported Git metadata.");
        }
        finally { Directory.Delete(link); }
    }

    private static async Task<string> GitAsync(string directory, string[] args, string? input = null)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = input is not null, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        start.Environment["GIT_ALLOW_PROTOCOL"] = "file";
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start synthetic Git.");
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            if (input is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(input), limit.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(limit.Token);
            var details = await error;
            Require(process.ExitCode == 0, $"Synthetic Git {args[0]} failed. {details}");
            return (await output).Trim();
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    internal static void RunInterruptedChild(string repository)
    {
        var full = Path.GetFullPath(repository);
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "YMMProjectManager-tests"));
        var relative = Path.GetRelativePath(allowed, full).Split(Path.DirectorySeparatorChar);
        if (relative.Length != 3 || !Guid.TryParseExact(relative[0], "N", out _) ||
            relative[1] != FixtureDirectory || relative[2] != "interrupted")
            throw new InvalidOperationException("Pull interruption requires the isolated fixture root.");

        using var fence = OwnedFilePublication.DirectoryFence.Open(Path.Combine(full, ".git"), enforceShare: true);
        using var journal = GitPullOperationJournal.Create(full, Approved);
        // Exit from the owned test process without running the normal completion or cleanup path.
        Environment.Exit(InterruptedExitCode);
    }

    private static async Task TestInterruptedChildAsync(string repository)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(ChildArgument);
        start.ArgumentList.Add(repository);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Pull recovery fixture.");
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await child.WaitForExitAsync(limit.Token);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }

        Require(child.ExitCode == InterruptedExitCode, "The fixture did not reach the interruption checkpoint.");
        var path = GitPullOperationJournal.GetPath(repository);
        var original = File.ReadAllBytes(path);
        using (var evidence = JsonDocument.Parse(original))
        {
            var record = evidence.RootElement;
            Require(record.GetProperty("Version").GetInt32() == 1 &&
                record.GetProperty("OperationId").GetGuid() != Guid.Empty &&
                record.GetProperty("State").GetString() == "MutationPrepared" &&
                record.GetProperty("BranchRef").GetString() == Approved.BranchRef &&
                record.GetProperty("StartObjectId").GetString() == Approved.StartObjectId &&
                record.GetProperty("RemoteName").GetString() == Approved.RemoteName &&
                record.GetProperty("UpstreamRef").GetString() == Approved.UpstreamRef &&
                record.GetProperty("TargetObjectId").GetString() == Approved.TargetObjectId,
                "The retained marker cannot identify its approved operation.");
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Require(GitPullOperationJournal.Exists(repository), "Recovery did not detect the interrupted marker.");
            RequireCreateRefused(repository);
            Require(original.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "Recovery changed the interrupted marker.");
        }

        VerifyFixture(repository);
    }

    private static Task TestOwnedJournalAsync(string repository)
    {
        Require(!GitPullOperationJournal.Exists(repository), "The fixture started with a marker.");
        using (var journal = GitPullOperationJournal.Create(repository, Approved))
        {
            Require(GitPullOperationJournal.Exists(repository), "The prepared marker was not visible.");
            RequireCreateRefused(repository);
            Require(journal.Clear(), "Clearing the exact owned record failed.");
        }

        Require(!GitPullOperationJournal.Exists(repository), "The verified journal remained after clearing.");
        using (var next = GitPullOperationJournal.Create(repository, Approved))
            Require(next.Clear(), "The next verified operation could not publish and clear its own marker.");
        VerifyFixture(repository);
        return Task.CompletedTask;
    }

    private static Task TestUnrecognizedMarkerAsync(string repository)
    {
        var path = GitPullOperationJournal.GetPath(repository);
        const string unknown = "unrecognized synthetic diagnostic record";
        File.WriteAllText(path, unknown);
        Require(GitPullOperationJournal.Exists(repository), "The unreadable outcome was silently ignored.");
        RequireCreateRefused(repository);
        Require(File.ReadAllText(path) == unknown, "An unrecognized journal was replaced or cleared.");
        VerifyFixture(repository);
        return Task.CompletedTask;
    }

    private static async Task TestWriterLeaseAsync(string repository)
    {
        using var first = GitAppWriteLease.TryAcquire(repository) ?? throw new InvalidOperationException("The writer gate was already owned.");
        var nested = Path.Combine(repository, "nested");
        using (var other = await Task.Run(() => GitAppWriteLease.TryAcquire(nested)))
            Require(other is null, "A second app writer acquired a lease during the first.");

        first.Dispose();
        using var successor = GitAppWriteLease.TryAcquire(nested) ?? throw new InvalidOperationException("The first writer did not release its lease.");
        first.Dispose();
        using var overlapping = GitAppWriteLease.TryAcquire(repository);
        Require(overlapping is null, "Disposing the retired lease released the current writer.");
        VerifyFixture(repository);
    }

    private static string CreateFixture(string parent, string name)
    {
        var root = Path.Combine(parent, name);
        var git = Directory.CreateDirectory(Path.Combine(root, ".git")).FullName;
        File.WriteAllText(Path.Combine(git, "HEAD"), HeadText);
        File.WriteAllText(Path.Combine(git, "index"), IndexText);
        File.WriteAllText(Path.Combine(git, "ymmpm-preserve.txt"), UnrelatedText);
        File.WriteAllText(Path.Combine(root, "素材.ymmp"), WorkingText);
        return root;
    }

    private static void VerifyFixture(string repository)
    {
        var git = Path.Combine(repository, ".git");
        Require(File.ReadAllText(Path.Combine(git, "HEAD")) == HeadText &&
            File.ReadAllText(Path.Combine(git, "index")) == IndexText &&
            File.ReadAllText(Path.Combine(git, "ymmpm-preserve.txt")) == UnrelatedText &&
            File.ReadAllText(Path.Combine(repository, "素材.ymmp")) == WorkingText,
            "The helper modified the fixture outside its owned journal.");
        Require(!Directory.EnumerateFileSystemEntries(git, ".ymmpm-pull-*.tmp").Any(),
            "An unsuccessful attempt left an owned temporary file.");
    }

    private static void RequireCreateRefused(string repository)
    {
        var alternate = Approved with { TargetObjectId = new string('3', 40) };
        try
        {
            using var competing = GitPullOperationJournal.Create(repository, alternate);
        }
        catch (IOException)
        {
            return;
        }

        throw new InvalidOperationException("A second journal was published over existing evidence.");
    }

    private static void Require(bool assertion, string message)
    {
        if (!assertion) throw new InvalidOperationException(message);
    }
}
