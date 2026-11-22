using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Git;

//CSJ,class=GitPullRecoveryTests,Gitプル復旧テスト
internal static class GitPullRecoveryTests
{
    //CSJ,field=ChildArgument,子引数
    internal const string ChildArgument = "--git-pull-recovery-child";
    internal const string AppliedChildArgument = "--git-pull-applied-recovery-child";
    //CSJ,field=FixtureDirectory,テスト用データディレクトリ
    private const string FixtureDirectory = "git-pull-recovery";
    //CSJ,field=InterruptedExitCode,中断済み終了コード
    private const int InterruptedExitCode = 86;
    //CSJ,field=HeadText,HEADテキスト
    private const string HeadText = "ref: refs/heads/test-main\n";
    //CSJ,field=IndexText,インデックステキスト
    private const string IndexText = "synthetic index bytes";
    //CSJ,field=WorkingText,作業中テキスト
    private const string WorkingText = "working project data";
    private const string UnrelatedText = "unrelated recovery evidence";
    //CSJ,field=Approved,承認済み
    private static readonly GitPullOperationJournal.ApprovalEvidence Approved = new(
        "refs/heads/test-main", new string('1', 40), "origin", "refs/heads/test-main", new string('2', 40));

    //CSJ,method=RunAsync,実行する（非同期）
    internal static async Task RunAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(workRoot, FixtureDirectory);
        await Program.RunNamedCaseAsync("Pull recovery retains the mutation marker after child process termination",
            () => TestInterruptedChildAsync(CreateFixture(root, "interrupted")));
        await Program.RunNamedCaseAsync("Pull restart preserves evidence after Git updated the checkout",
            () => TestInterruptedAfterMergeAsync(root));
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
            //CSJ,method=CanApply,適用が可能かを判定する
            static bool CanApply(string tree) => GitPullWindowsTree.TryReadFilePaths(tree, out _);
            Require(CanApply(TreeEntry("素材") + FileEntry("素材/台本.ymmp", "100755")),
                "A normal Unicode tree cannot be checked out.");
            Require(!CanApply(FileEntry("Cover.txt") + FileEntry("cover.txt")) &&
                !CanApply(TreeEntry("Media") + TreeEntry("media")),
                "A case-distinct path collision was accepted.");
            Require(!CanApply(FileEntry("media/COM¹.wav")) &&
                !CanApply(TreeEntry(".Git")) &&
                !CanApply(FileEntry("media/name.")) &&
                !CanApply(FileEntry("media/invalid\uFFFD.wav")) &&
                !CanApply(FileEntry("media/name ", "120000")) &&
                !CanApply(FileEntry("media/submodule", "160000")) &&
                !CanApply(FileEntry("unterminated")[..^1]),
                "An unsupported Win32 path, mode or malformed tree was accepted.");
            return Task.CompletedTask;
        });
        await Program.RunNamedCaseAsync("Confirmed Pull refuses case collisions and targets that require checkout filters",
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
            //CSJ,variable=worktree,作業ツリー
            var worktree = Directory.CreateDirectory(Path.Combine(root, "diagnostic")).FullName;
            await GitAsync(worktree, ["init", "-b", "main"]);
            await GitAsync(worktree, ["config", "user.name", "Synthetic Git Fixture"]);
            await GitAsync(worktree, ["config", "user.email", "fixture@example.invalid"]);
            await File.WriteAllTextAsync(Path.Combine(worktree, "project.ymmp"), "{}");
            await GitAsync(worktree, ["add", "--", "project.ymmp"]);
            await GitAsync(worktree, ["commit", "-m", "baseline"]);
            //CSJ,variable=repository,リポジトリ
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

    //CSJ,method=CreatePullFixtureAsync,プルテスト用データを作成する（非同期）
    private static async Task<(string Seed, string Remote, string Worktree, string Base)> CreatePullFixtureAsync(
        //CSJ,parameter=root,ルート
        string root, string name)
    {
        var seed = Directory.CreateDirectory(Path.Combine(root, name + "-seed")).FullName;
        //CSJ,variable=remote,リモート
        var remote = Path.Combine(root, name + "-remote.git");
        //CSJ,variable=worktree,作業ツリー
        var worktree = Path.Combine(root, name);
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
        return (seed, remote, worktree, starting);
    }

    private static async Task TestCaseCollidingTargetAsync(string root)
    {
        var (seed, remote, worktree, starting) = await CreatePullFixtureAsync(root, "case-作業コピー");
        //CSJ,variable=indexPath,インデックスパス
        var indexPath = Path.Combine(worktree, ".git", "index");
        //CSJ,variable=index,インデックス
        var index = await File.ReadAllBytesAsync(indexPath);
        var original = await File.ReadAllBytesAsync(Path.Combine(worktree, "keep.ymmp"));

        var first = await GitAsync(seed, ["hash-object", "-w", "--stdin"], "first");
        var second = await GitAsync(seed, ["hash-object", "-w", "--stdin"], "second");
        var originalBlob = await GitAsync(seed, ["rev-parse", "HEAD:keep.ymmp"]);
        //CSJ,variable=tree,ツリー
        var tree = await GitAsync(seed, ["mktree", "-z"],
            $"100644 blob {first}\tCover.txt\0" +
            $"100644 blob {second}\tcover.txt\0" +
            $"100644 blob {originalBlob}\tkeep.ymmp\0");
        var target = await GitAsync(seed, ["commit-tree", tree, "-p", starting], "case-distinct synthetic files");
        await GitAsync(seed, ["push", "--", remote, target + ":refs/heads/main"]);

        //CSJ,variable=service,サービス
        var service = new GitRepository(worktree);
        //CSJ,variable=remotePreview,リモートプレビュー
        var remotePreview = await service.InspectPullRemoteAsync();
        Require(remotePreview.Success && remotePreview.PullRemote is not null, "Cannot bind the isolated upstream.");
        await AssertMetadataJunctionRejectedAsync(root, worktree, service);
        //CSJ,variable=preview,プレビュー
        var preview = await service.InspectPullTargetAsync(remotePreview.PullRemote!);
        Require(preview.Success && preview.PullTarget?.TargetObjectId == target, "The approved commit was not observed.");
        var result = await service.PullTrustedAsync(preview.PullTarget!);
        Require(!result.Success && result.Failure == GitFailure.UnsafeOperation &&
            result.StandardError.Contains("paths or file types", StringComparison.Ordinal) &&
            !GitPullOperationJournal.Exists(worktree),
            $"The Windows tree guard did not reject the collision at preflight ({result.ExitCode}, {result.Failure}, {result.StandardError}).");
        //CSJ,variable=finalIndex,最終インデックス
        var finalIndex = await File.ReadAllBytesAsync(indexPath);
        //CSJ,variable=finalOriginal,最終元の
        var finalOriginal = await File.ReadAllBytesAsync(Path.Combine(worktree, "keep.ymmp"));
        Require(await GitAsync(worktree, ["rev-parse", "HEAD"]) == starting &&
            await GitAsync(worktree, ["symbolic-ref", "HEAD"]) == "refs/heads/main" &&
            index.SequenceEqual(finalIndex) && original.SequenceEqual(finalOriginal) &&
            !File.Exists(Path.Combine(worktree, "Cover.txt")), "A rejected target changed the branch, index or worktree.");

        await File.WriteAllTextAsync(Path.Combine(seed, ".gitattributes"), "*.wav filter=unspecified\n");
        await File.WriteAllTextAsync(Path.Combine(seed, "素材.wav"), "synthetic material");
        await GitAsync(seed, ["add", "--", ".gitattributes", "素材.wav"]);
        await GitAsync(seed, ["commit", "-m", "declare an external filter without configuring a program"]);
        await GitAsync(seed, ["push", "--", remote, "HEAD:refs/heads/fixture-filter"]);
        await GitAsync(worktree, ["config", "branch.main.merge", "refs/heads/fixture-filter"]);
        remotePreview = await service.InspectPullRemoteAsync();
        Require(remotePreview.Success && remotePreview.PullRemote is not null, "Cannot bind the filter fixture upstream.");
        preview = await service.InspectPullTargetAsync(remotePreview.PullRemote!);
        Require(preview.Success && preview.PullTarget is not null, "Cannot preview the filter fixture commit.");
        result = await service.PullTrustedAsync(preview.PullTarget!);
        finalIndex = await File.ReadAllBytesAsync(indexPath);
        Require(!result.Success && result.Failure == GitFailure.UnsafeOperation &&
            result.StandardError.Contains("external checkout filter", StringComparison.Ordinal) &&
            await GitAsync(worktree, ["rev-parse", "HEAD"]) == starting && index.SequenceEqual(finalIndex) &&
            !File.Exists(Path.Combine(worktree, "素材.wav")) && !GitPullOperationJournal.Exists(worktree),
            $"A filter-dependent tree reached checkout or failed with an unrelated reason ({result.Failure}, {result.StandardError}).");
    }

    private static async Task AssertMetadataJunctionRejectedAsync(string root, string worktree, GitRepository repository)
    {
        var target = Directory.CreateDirectory(Path.Combine(root, "junction-target")).FullName;
        //CSJ,variable=link,リンク
        var link = Path.Combine(worktree, ".git", "objects", "reserved-fixture");
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
        //CSJ,variable=process,プロセス
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start junction fixture.");
        //CSJ,variable=limit,上限
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        //CSJ,variable=error,エラー
        var error = process.StandardError.ReadToEndAsync(limit.Token);
        try { await process.WaitForExitAsync(limit.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        await output;
        //CSJ,variable=details,詳細
        var details = await error;
        Require(process.ExitCode == 0, $"Could not create a junction inside the synthetic checkout. {details}");
        try
        {
            //CSJ,variable=blocked,停止
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
        //CSJ,variable=key,キー
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        start.Environment["GIT_ALLOW_PROTOCOL"] = "file";
        foreach (var arg in args) start.ArgumentList.Add(arg);
        //CSJ,variable=process,プロセス
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start synthetic Git.");
        //CSJ,variable=limit,上限
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            //CSJ,variable=error,エラー
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            if (input is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(input), limit.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(limit.Token);
            //CSJ,variable=details,詳細
            var details = await error;
            Require(process.ExitCode == 0, $"Synthetic Git {args[0]} failed. {details}");
            return (await output).Trim();
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static string RequireIsolatedRepository(string repository, string name)
    {
        //CSJ,variable=full,絶対パス
        var full = Path.GetFullPath(repository);
        //CSJ,variable=allowed,許可済み
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "YMMProjectManager-tests"));
        //CSJ,variable=relative,相対
        var relative = Path.GetRelativePath(allowed, full).Split(Path.DirectorySeparatorChar);
        if (relative.Length != 3 || !Guid.TryParseExact(relative[0], "N", out _) ||
            relative[1] != FixtureDirectory || relative[2] != name)
            throw new InvalidOperationException("Pull interruption requires the isolated fixture root.");
        return full;
    }

    //CSJ,method=RunInterruptedChild,中断済み子を実行する
    internal static void RunInterruptedChild(string repository)
    {
        var full = RequireIsolatedRepository(repository, "interrupted");
        //CSJ,variable=fence,整合性境界
        using var fence = OwnedFilePublication.DirectoryFence.Open(Path.Combine(full, ".git"), enforceShare: true);
        //CSJ,variable=journal,作業記録
        using var journal = GitPullOperationJournal.Create(full, Approved);
        // Exit from the owned test process without running the normal completion or cleanup path.
        Environment.Exit(InterruptedExitCode);
    }

    //CSJ,method=RunInterruptedAfterMergeAsync,中断済み変更後マージを実行する（非同期）
    internal static async Task RunInterruptedAfterMergeAsync(string repository)
    {
        var full = RequireIsolatedRepository(repository, "interrupted-applied");
        //CSJ,variable=remotePath,リモートパス
        var remotePath = Path.Combine(Path.GetDirectoryName(full)!, "interrupted-applied-remote.git");
        //CSJ,variable=fence,整合性境界
        using var fence = OwnedFilePublication.DirectoryFence.Open(Path.Combine(full, ".git"), enforceShare: true);
        //CSJ,variable=remoteFence,リモート整合性境界
        using var remoteFence = OwnedFilePublication.DirectoryFence.Open(remotePath, enforceShare: true);
        //CSJ,variable=service,サービス
        var service = new GitRepository(full) { AfterPullProcessExit = () => Environment.Exit(InterruptedExitCode) };
        //CSJ,variable=endpoint,エンドポイント
        var endpoint = await service.InspectPullRemoteAsync();
        Require(endpoint.PullRemote is { } remote && Path.IsPathFullyQualified(remote.Endpoint) &&
            string.Equals(Path.GetFullPath(remote.Endpoint), remotePath, StringComparison.OrdinalIgnoreCase) &&
            remote.BranchRef == "refs/heads/main" &&
            remote.UpstreamRef == "refs/heads/main", "The child checkout must use only its isolated remote.");
        //CSJ,variable=preview,プレビュー
        var preview = await service.InspectPullTargetAsync(endpoint.PullRemote!);
        Require(preview.PullTarget is { ChangesWorktree: true }, "The child requires a new approved commit.");
        await service.PullTrustedAsync(preview.PullTarget!);
        throw new InvalidOperationException("The child did not reach the post-merge checkpoint.");
    }

    //CSJ,method=TestInterruptedAfterMergeAsync,中断済み変更後マージをテストする（非同期）
    private static async Task TestInterruptedAfterMergeAsync(string root)
    {
        var (seed, remote, worktree, _) = await CreatePullFixtureAsync(root, "interrupted-applied");
        await File.WriteAllTextAsync(Path.Combine(seed, "keep.ymmp"), "{\"version\":2}");
        await GitAsync(seed, ["add", "--", "keep.ymmp"]);
        await GitAsync(seed, ["commit", "-m", "update"]);
        await GitAsync(seed, ["push", "--", remote, "main"]);
        var desired = await GitAsync(seed, ["rev-parse", "HEAD"]);

        await RunChildProcessAsync(AppliedChildArgument, worktree);
        //CSJ,variable=marker,マーカー
        var marker = GitPullOperationJournal.GetPath(worktree);
        //CSJ,variable=bytes,バイト列
        var bytes = await File.ReadAllBytesAsync(marker);
        //CSJ,variable=receipt,照合記録
        using var receipt = JsonDocument.Parse(bytes);
        Require(receipt.RootElement.GetProperty("TargetObjectId").GetString() == desired &&
            await GitAsync(worktree, ["rev-parse", "refs/heads/main"]) == desired &&
            await GitAsync(worktree, ["diff-index", "--cached", "--quiet", desired, "--"]) == "" &&
            await GitAsync(worktree, ["--no-optional-locks", "status", "--porcelain"]) == "" &&
            await File.ReadAllTextAsync(Path.Combine(worktree, "keep.ymmp")) == "{\"version\":2}",
            "The interrupted product process did not leave the observed updated ref, index and worktree.");
        var restarted = new GitRepository(worktree);
        //CSJ,variable=attempt,試行
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await restarted.InspectPullRemoteAsync();
            Require(result.Failure == GitFailure.OutcomeUnknown && result.RecoveryPath == marker,
                "Restart approved a second Pull without verifying the previous outcome.");
        }
        var remaining = await File.ReadAllBytesAsync(marker);
        Require(bytes.SequenceEqual(remaining), "Recovery did not preserve the original operation record.");
    }

    //CSJ,method=RunChildProcessAsync,子プロセスを実行する（非同期）
    private static async Task RunChildProcessAsync(string argument, string repository)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(argument);
        start.ArgumentList.Add(repository);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Pull recovery fixture.");
        try
        {
            //CSJ,variable=limit,上限
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await child.WaitForExitAsync(limit.Token);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }

        Require(child.ExitCode == InterruptedExitCode, "The fixture did not reach the interruption checkpoint.");
    }

    //CSJ,method=TestInterruptedChildAsync,中断済み子をテストする（非同期）
    private static async Task TestInterruptedChildAsync(string repository)
    {
        await RunChildProcessAsync(ChildArgument, repository);
        //CSJ,variable=path,パス
        var path = GitPullOperationJournal.GetPath(repository);
        var original = File.ReadAllBytes(path);
        //CSJ,variable=evidence,証跡
        using (var evidence = JsonDocument.Parse(original))
        {
            //CSJ,variable=record,記録
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

        //CSJ,variable=attempt,試行
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Require(GitPullOperationJournal.Exists(repository), "Recovery did not detect the interrupted marker.");
            RequireCreateRefused(repository);
            Require(original.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "Recovery changed the interrupted marker.");
        }

        VerifyFixture(repository);
    }

    //CSJ,method=TestOwnedJournalAsync,管理対象の作業記録をテストする（非同期）
    private static Task TestOwnedJournalAsync(string repository)
    {
        Require(!GitPullOperationJournal.Exists(repository), "The fixture started with a marker.");
        //CSJ,variable=journal,作業記録
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
        //CSJ,variable=path,パス
        var path = GitPullOperationJournal.GetPath(repository);
        //CSJ,variable=unknown,不明
        const string unknown = "unrecognized synthetic diagnostic record";
        File.WriteAllText(path, unknown);
        Require(GitPullOperationJournal.Exists(repository), "The unreadable outcome was silently ignored.");
        RequireCreateRefused(repository);
        Require(File.ReadAllText(path) == unknown, "An unrecognized journal was replaced or cleared.");
        VerifyFixture(repository);
        return Task.CompletedTask;
    }

    //CSJ,method=TestWriterLeaseAsync,書き込み処理リースをテストする（非同期）
    private static async Task TestWriterLeaseAsync(string repository)
    {
        using var first = GitAppWriteLease.TryAcquire(repository) ?? throw new InvalidOperationException("The writer gate was already owned.");
        //CSJ,variable=nested,入れ子
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

    //CSJ,method=CreateFixture,テスト用データを作成する
    private static string CreateFixture(string parent, string name)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(parent, name);
        var git = Directory.CreateDirectory(Path.Combine(root, ".git")).FullName;
        File.WriteAllText(Path.Combine(git, "HEAD"), HeadText);
        File.WriteAllText(Path.Combine(git, "index"), IndexText);
        File.WriteAllText(Path.Combine(git, "ymmpm-preserve.txt"), UnrelatedText);
        File.WriteAllText(Path.Combine(root, "素材.ymmp"), WorkingText);
        return root;
    }

    //CSJ,method=VerifyFixture,テスト用データを検証する
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
