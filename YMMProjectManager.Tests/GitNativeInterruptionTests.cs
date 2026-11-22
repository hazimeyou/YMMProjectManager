using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Git;

//CSJ,class=GitNativeInterruptionTests,ネイティブGitの中断テスト
internal static class GitNativeInterruptionTests
{
    internal const string RestartArgument = "--git-native-interruption-restart";
    //CSJ,field=FixtureDirectory,テスト用データディレクトリ
    private const string FixtureDirectory = "native-pull";
    //CSJ,field=WorktreeName,作業ツリー名前
    private const string WorktreeName = "checkout";
    private const string UpdatesDirectory = "incoming";
    //CSJ,field=OriginalFile,元のファイル
    private const string OriginalFile = "元プロジェクト.ymmp";
    private const string IgnoredFile = "unrelated.local";
    private const string UntrackedFile = "concurrent-note.txt";
    private const int IncomingFileCount = 4096;

    //CSJ,method=RunAsync,実行する（非同期）
    internal static Task RunAsync(string workRoot) => Program.RunNamedCaseAsync(
        "Confirmed Pull retains an UNKNOWN result when native Git is killed after changing worktree files",
        () => InterruptDuringUpdateAsync(workRoot));

    private static async Task InterruptDuringUpdateAsync(string workRoot)
    {
        //CSJ,variable=allowed,許可済み
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "YMMProjectManager-tests"));
        if (!Guid.TryParseExact(Path.GetRelativePath(allowed, workRoot), "N", out _))
            throw new InvalidOperationException("Native Pull setup requires the isolated test run directory.");
        //CSJ,variable=fixtureFence,テスト用データ整合性境界
        using var fixtureFence = OwnedFilePublication.DirectoryFence.Open(workRoot, enforceShare: true);
        //CSJ,variable=root,ルート
        var root = Directory.CreateDirectory(Path.Combine(workRoot, FixtureDirectory)).FullName;
        var seed = Directory.CreateDirectory(Path.Combine(root, "seed")).FullName;
        var bare = Path.Combine(root, "remote.git");
        var checkout = Path.Combine(root, WorktreeName);
        await GitAsync(seed, ["init", "-b", "main"]);
        await GitAsync(seed, ["config", "user.name", "Synthetic Pull Test"]);
        await GitAsync(seed, ["config", "user.email", "synthetic@example.invalid"]);
        await File.WriteAllTextAsync(Path.Combine(seed, OriginalFile), """{"Name":"synthetic original"}""");
        await File.WriteAllTextAsync(Path.Combine(seed, ".gitignore"), "*.local\n");
        await GitAsync(seed, ["add", "--", OriginalFile, ".gitignore"]);
        await GitAsync(seed, ["commit", "-m", "original"]);
        var start = await GitAsync(seed, ["rev-parse", "HEAD"]);
        await GitAsync(root, ["init", "--bare", "-b", "main", bare]);
        await GitAsync(seed, ["push", "--", bare, "main"]);
        await GitAsync(root, ["clone", "--", bare, checkout]);
        RequireIsolatedCheckout(checkout);
        await GitAsync(checkout, ["config", "checkout.workers", "1"]);

        // Reuse one blob to keep fixture setup small while Git still has a set of real files to write.
        //CSJ,variable=payload,本体データ
        var payload = new string('x', 32 * 1024);
        //CSJ,variable=payloadHash,本体データハッシュ
        var payloadHash = Digest(Encoding.UTF8.GetBytes(payload));
        var blob = await GitAsync(seed, ["hash-object", "-w", "--stdin"], payload);
        //CSJ,variable=files,ファイル一覧
        var files = new StringBuilder();
        for (var i = 0; i < IncomingFileCount; i++)
            files.Append($"100644 blob {blob}\tpayload-{i:D4}.bin\0");
        var subtree = await GitAsync(seed, ["mktree", "-z"], files.ToString());
        //CSJ,variable=originalTree,元のツリー
        var originalTree = await GitAsync(seed, ["ls-tree", "-z", "HEAD"], trim: false);
        //CSJ,variable=targetTree,対象ツリー
        var targetTree = await GitAsync(seed, ["mktree", "-z"], originalTree + $"040000 tree {subtree}\t{UpdatesDirectory}\0");
        var target = await GitAsync(seed, ["commit-tree", targetTree, "-p", start], "synthetic native update\n");
        await GitAsync(seed, ["push", "--", bare, target + ":refs/heads/main"]);

        //CSJ,variable=originalPath,元のパス
        var originalPath = Path.Combine(checkout, OriginalFile);
        var ignoredPath = Path.Combine(checkout, IgnoredFile);
        var untrackedPath = Path.Combine(checkout, UntrackedFile);
        //CSJ,variable=indexPath,インデックスパス
        var indexPath = Path.Combine(checkout, ".git", "index");
        //CSJ,variable=indexLockPath,インデックスロックパス
        var indexLockPath = indexPath + ".lock";
        const string ignoredContents = "ignored synthetic fixture data";
        const string untrackedContents = "untracked data created by the test during mutation";
        await File.WriteAllTextAsync(ignoredPath, ignoredContents);
        //CSJ,variable=initialIndex,初期インデックス
        var initialIndex = Digest(await File.ReadAllBytesAsync(indexPath));
        //CSJ,variable=initialEntries,初期項目一覧
        var initialEntries = await GitAsync(checkout, ["ls-files", "--stage", "-z"], trim: false);
        //CSJ,variable=originalHash,元のハッシュ
        var originalHash = Digest(await File.ReadAllBytesAsync(originalPath));
        var ignoredHash = Digest(await File.ReadAllBytesAsync(ignoredPath));
        Require(await GitAsync(checkout, ["--no-optional-locks", "status", "--porcelain"]) == "" && !File.Exists(indexLockPath),
            "The isolated checkout must start clean with a preserved ignored file.");

        //CSJ,variable=service,サービス
        var service = new GitRepository(checkout);
        //CSJ,variable=endpoint,エンドポイント
        var endpoint = await service.InspectPullRemoteAsync();
        Require(endpoint.Success && endpoint.PullRemote is { } remote &&
            string.Equals(remote.Endpoint, bare, StringComparison.OrdinalIgnoreCase), "The Pull fixture did not bind its isolated remote.");
        //CSJ,variable=preview,プレビュー
        var preview = await service.InspectPullTargetAsync(endpoint.PullRemote!);
        Require(preview.Success && preview.PullTarget is { ChangesWorktree: true } approved &&
            approved.StartObjectId == start && approved.TargetObjectId == target, "The Pull fixture must approve the exact target.");

        var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = Path.Combine(checkout, UpdatesDirectory);
        using var watcher = new FileSystemWatcher(checkout, "*.bin")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024,
        };
        watcher.Created += Observe;
        watcher.Changed += Observe;
        watcher.Error += (_, e) => observed.TrySetException(e.GetException());
        watcher.EnableRaisingEvents = true;

        (string File, bool IndexLockWasPresent, int ExitCode)? interruption = null;
        service.AfterPullProcessStarted = process =>
        {
            Require(process.StartInfo.WorkingDirectory == checkout && process.StartInfo.ArgumentList.Contains("merge") &&
                process.StartInfo.ArgumentList.Contains(target), "The intervention must target only the actual approved synthetic merge child.");
            try
            {
                //CSJ,variable=path,パス
                var path = observed.Task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
                Require(!process.HasExited, "Native Git exited before the file observation could be used.");
                File.WriteAllText(untrackedPath, untrackedContents);
                var lockBeforeStop = File.Exists(indexLockPath);
                Require(!process.HasExited, "Native Git exited before termination after observing the worktree change.");
                process.Kill();
                // On Windows Process.Kill supplies exit code -1. A natural ref-sharing error does not count.
                Require(process.WaitForExit(10_000) && process.ExitCode == -1, "The observed Git child did not exit through the forced termination.");
                interruption = (path, lockBeforeStop, process.ExitCode);
            }
            finally
            {
                // Stop only the owned test child even if observation failed. Such a stop is not test evidence.
                if (!process.HasExited)
                {
                    process.Kill();
                    if (!process.WaitForExit(10_000))
                        throw new TimeoutException("The owned native Git child did not stop after the fixture failed.");
                }
            }
        };

        // A synthetic observer retains a read-only ref handle across the child lifetime.
        // Windows refuses replacing the ref while it is held, independently of watcher scheduling.
        //CSJ,variable=externalRefReader,外部参照読み取り元
        FileStream? externalRefReader = null;
        service.AfterPullJournalCreated = () =>
            externalRefReader = new FileStream(Path.Combine(checkout, ".git", "refs", "heads", "main"),
                FileMode.Open, FileAccess.Read, FileShare.Read);

        GitResult result;
        try { result = await service.PullTrustedAsync(preview.PullTarget!); }
        finally { externalRefReader?.Dispose(); }
        var stopped = interruption ?? throw new InvalidOperationException(
            $"The fixture did not terminate an observed live native Git update ({result.Failure}, {result.StandardError}).");
        Require(result.Failure == GitFailure.OutcomeUnknown, $"Interrupted checkout was not UNKNOWN ({result.Failure}).");

        //CSJ,variable=marker,マーカー
        var marker = GitPullOperationJournal.GetPath(checkout);
        //CSJ,variable=markerBytes,マーカーバイト列
        var markerBytes = await File.ReadAllBytesAsync(marker);
        //CSJ,variable=evidence,証跡
        using var evidence = JsonDocument.Parse(markerBytes);
        //CSJ,variable=record,記録
        var record = evidence.RootElement;
        Require(result.RecoveryPath == marker && record.GetProperty("OperationId").GetGuid() != Guid.Empty &&
            record.GetProperty("StartObjectId").GetString() == start && record.GetProperty("TargetObjectId").GetString() == target,
            "The exact interrupted operation was not retained.");

        //CSJ,variable=finalBranch,最終ブランチ
        var finalBranch = await GitAsync(checkout, ["symbolic-ref", "HEAD"]);
        //CSJ,variable=finalHead,最終HEAD
        var finalHead = await GitAsync(checkout, ["rev-parse", "HEAD"]);
        //CSJ,variable=finalIndex,最終インデックス
        var finalIndex = Digest(await File.ReadAllBytesAsync(indexPath));
        //CSJ,variable=finalEntries,最終項目一覧
        var finalEntries = await GitAsync(checkout, ["ls-files", "--stage", "-z"], trim: false);
        //CSJ,variable=worktreeStatus,作業ツリー状態
        var worktreeStatus = await GitAsync(checkout, ["--no-optional-locks", "status", "--porcelain"]);
        var appliedFiles = Directory.EnumerateFiles(updates, "*.bin").Count();
        //CSJ,variable=indexMatchesTarget,インデックス一致項目対象
        var indexMatchesTarget = await GitAsync(checkout, ["diff-index", "--cached", "--name-only", target, "--"]) == "";
        //CSJ,variable=finalIndexLock,最終インデックスロック
        var finalIndexLock = File.Exists(indexLockPath) ? Digest(await File.ReadAllBytesAsync(indexLockPath)) : null;
        Require(finalBranch == "refs/heads/main" && finalHead == start,
            "The interruption reached an unexpected branch or commit.");
        Require(appliedFiles > 0 && worktreeStatus.Length != 0 &&
            Digest(await File.ReadAllBytesAsync(stopped.File)) == payloadHash &&
            (appliedFiles < IncomingFileCount || !indexMatchesTarget || finalHead != target),
            $"Native interruption evidence must include an incomplete update. Files={appliedFiles}/{IncomingFileCount}, " +
            $"indexMatchesTarget={indexMatchesTarget}, headMatchesTarget={finalHead == target}, " +
            $"indexLockBeforeStop={stopped.IndexLockWasPresent}, indexLockAfter={finalIndexLock is not null}.");
        Require(Digest(await File.ReadAllBytesAsync(originalPath)) == originalHash &&
            Digest(await File.ReadAllBytesAsync(ignoredPath)) == ignoredHash &&
            await File.ReadAllTextAsync(untrackedPath) == untrackedContents, "An existing file or concurrent synthetic note was changed.");

        // Re-enter the normal product preflight in a newly launched test process.
        await RunRestartProbeAsync(checkout);
        var retried = await new GitRepository(checkout).PullTrustedAsync(preview.PullTarget!);
        var retainedMarker = await File.ReadAllBytesAsync(marker);
        Require(retried.Failure == GitFailure.OutcomeUnknown && retried.RecoveryPath == marker &&
            markerBytes.SequenceEqual(retainedMarker) &&
            finalHead == await GitAsync(checkout, ["rev-parse", "HEAD"]) &&
            finalIndex == Digest(await File.ReadAllBytesAsync(indexPath)) &&
            worktreeStatus == await GitAsync(checkout, ["--no-optional-locks", "status", "--porcelain"]) &&
            finalIndexLock == (File.Exists(indexLockPath) ? Digest(await File.ReadAllBytesAsync(indexLockPath)) : null),
            "Restart or retry changed the observed state or the operation record.");

        Console.WriteLine($"Native Pull termination: exit={stopped.ExitCode}, headBefore={start}, headAfter={finalHead}, target={target}, " +
            $"indexBefore={initialIndex}, indexAfter={finalIndex}, sameStagedEntries={initialEntries == finalEntries}, indexMatchesTarget={indexMatchesTarget}, " +
            $"indexLockBeforeStop={stopped.IndexLockWasPresent}, indexLockAfter={finalIndexLock is not null}, " +
            $"materialized={appliedFiles}/{IncomingFileCount}, originalSha256={originalHash}, ignoredSha256={ignoredHash}, " +
            $"untrackedSha256={Digest(await File.ReadAllBytesAsync(untrackedPath))}, outcome={result.Failure}, restartRefused=true");

        //CSJ,method=Observe,観測する
        void Observe(object _, FileSystemEventArgs change)
        {
            if (observed.Task.IsCompleted || Path.GetDirectoryName(change.FullPath) != updates) return;
            try
            {
                //CSJ,variable=file,ファイル
                using var file = new FileStream(change.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length == payload.Length && Convert.ToHexString(SHA256.HashData(file)) == payloadHash)
                    observed.TrySetResult(change.FullPath);
            }
            catch (IOException) { /* A file may still be opening or being written. Observe a later event. */ }
        }
    }

    internal static async Task VerifyRestartAsync(string checkout)
    {
        var full = RequireIsolatedCheckout(checkout);
        //CSJ,variable=fence,整合性境界
        using var fence = OwnedFilePublication.DirectoryFence.Open(Path.Combine(full, ".git"), enforceShare: true);
        //CSJ,variable=service,サービス
        var service = new GitRepository(full);
        //CSJ,variable=attempt,試行
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await service.InspectPullRemoteAsync();
            Require(result.Failure == GitFailure.OutcomeUnknown && result.PullRemote is null &&
                result.RecoveryPath == GitPullOperationJournal.GetPath(full), "A fresh process prepared another Pull despite the recovery record.");
        }
    }

    private static string RequireIsolatedCheckout(string checkout)
    {
        //CSJ,variable=full,絶対パス
        var full = Path.GetFullPath(checkout);
        //CSJ,variable=allowed,許可済み
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "YMMProjectManager-tests"));
        //CSJ,variable=relative,相対
        var relative = Path.GetRelativePath(allowed, full).Split(Path.DirectorySeparatorChar);
        if (relative.Length != 3 || !Guid.TryParseExact(relative[0], "N", out _) ||
            relative[1] != FixtureDirectory || relative[2] != WorktreeName)
            throw new InvalidOperationException("Native Pull interruption requires the isolated test checkout.");
        return full;
    }

    private static async Task RunRestartProbeAsync(string checkout)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
        };
        start.ArgumentList.Add(RestartArgument);
        start.ArgumentList.Add(checkout);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the restart probe.");
        //CSJ,variable=limit,上限
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await child.WaitForExitAsync(limit.Token); }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
        Require(child.ExitCode == 0, $"The restarted process did not report safe recovery ({child.ExitCode}).");
    }

    private static async Task<string> GitAsync(string directory, string[] args, string? input = null, bool trim = true)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = input is not null, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        //CSJ,variable=name,名前
        foreach (var name in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        start.Environment["GIT_ALLOW_PROTOCOL"] = "file";
        foreach (var arg in args) start.ArgumentList.Add(arg);
        //CSJ,variable=process,プロセス
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start synthetic Git.");
        //CSJ,variable=limit,上限
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
            //CSJ,variable=text,テキスト
            var text = await output;
            return trim ? text.Trim() : text;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void Require(bool condition, string explanation)
    {
        if (!condition) throw new InvalidOperationException(explanation);
    }
}
