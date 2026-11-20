using System.Diagnostics;
using System.IO;
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
