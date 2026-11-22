using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Git;

//CSJ,class=GitHttpsAcceptanceTests,Git HTTPS受入テスト
internal static class GitHttpsAcceptanceTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    public static async Task RunAsync(string workRoot)
    {
        //CSJ,variable=fullWorkRoot,完全作業ルート
        var fullWorkRoot = Path.GetFullPath(workRoot);
        //CSJ,variable=expectedParent,期待値親
        var expectedParent = Path.Combine(Path.GetTempPath(), "YMMProjectManager-tests");
        Check(Guid.TryParseExact(Path.GetFileName(fullWorkRoot), "N", out _) &&
            string.Equals(Path.GetDirectoryName(fullWorkRoot), expectedParent, StringComparison.OrdinalIgnoreCase),
            "HTTPS acceptance requires the isolated runner's GUID fixture root.");
        //CSJ,variable=fixtureFence,テスト用データ整合性境界
        using var fixtureFence = OwnedFilePublication.DirectoryFence.Open(fullWorkRoot, enforceShare: true);
        //CSJ,variable=modeRoot,モードルート
        var modeRoot = Directory.CreateDirectory(Path.Combine(fullWorkRoot, "https-pull")).FullName;
        //CSJ,variable=serverRoot,サーバールート
        var serverRoot = Directory.CreateDirectory(Path.Combine(modeRoot, "server")).FullName;
        var bare = Path.Combine(serverRoot, "fixture.git");
        var source = Path.Combine(modeRoot, "source");
        var checkout = Path.Combine(modeRoot, "checkout");
        //CSJ,variable=environment,環境
        using var environment = new FixtureEnvironment();
        await GitAsync(modeRoot, "init", "--initial-branch=main", source);
        await GitAsync(serverRoot, "init", "--bare", "--initial-branch=main", bare);
        await GitAsync(source, "config", "user.name", "Loopback HTTPS fixture");
        await GitAsync(source, "config", "user.email", "fixture@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(source, ".gitignore"), "untouched.bin\n");
        var old = await CommitAsync("original");
        await GitAsync(source, "push", bare, "main:refs/heads/main");
        await GitAsync(modeRoot, "clone", "--no-local", "--", bare, checkout);
        await File.WriteAllTextAsync(Path.Combine(checkout, "untouched.bin"), "ignored user fixture");
        var target = await CommitAsync("accepted");
        var next = await CommitAsync("subsequent");
        await GitAsync(source, "push", bare, target + ":refs/heads/fixture-accepted", next + ":refs/heads/fixture-subsequent");
        await SetRemoteTipAsync(target);
        //CSJ,variable=baseline,基準
        var baseline = await SnapshotAsync(checkout);
        Check(baseline.Head == old, "unexpected fixture baseline");

        //CSJ,variable=authority,権限の根拠
        var authority = WriteCertificateFixture(modeRoot);
        //CSJ,variable=verifiedPeer,検証済み対向先
        await using var verifiedPeer = await HttpsServer.StartAsync(modeRoot, serverRoot, "verified");
        await using var wrongPeer = await HttpsServer.StartAsync(modeRoot, serverRoot, "wrong");
        await ConfigureEndpointAsync(verifiedPeer.Endpoint);
        var production = new GitRepository(checkout);
        var git = new GitRepository(checkout) { LoopbackPullCertificateAuthorityFile = authority };

        await Program.RunNamedCaseAsync("HTTPS Pull rejects an untrusted certificate with stock Git trust", async () =>
        {
            //CSJ,variable=remote,リモート
            var remote = await RequireRemoteAsync(production);
            var before = verifiedPeer.RequestCount;
            var refusal = await production.InspectPullTargetAsync(remote);
            Check(!refusal.Success && refusal.PullTarget is null && verifiedPeer.RequestCount == before,
                "stock trust reached the untrusted HTTP peer");
            await AssertPreservedAsync("untrusted certificate", refusal);
        });
        await Program.RunNamedCaseAsync("HTTPS Pull rejects a trusted certificate for a different IP", async () =>
        {
            await ConfigureEndpointAsync(wrongPeer.Endpoint);
            try
            {
                //CSJ,variable=remote,リモート
                var remote = await RequireRemoteAsync(git);
                var refusal = await git.InspectPullTargetAsync(remote);
                Check(!refusal.Success && refusal.PullTarget is null && wrongPeer.RequestCount == 0,
                    "IP mismatch reached the HTTP peer despite enabled certificate verification");
                await AssertPreservedAsync("certificate IP mismatch", refusal);
            }
            finally { await ConfigureEndpointAsync(verifiedPeer.Endpoint); }
        });
        await Program.RunNamedCaseAsync("HTTPS Pull distinguishes HTTP 401 and 403 without changing the checkout", async () =>
        {
            //CSJ,variable=remote,リモート
            var remote = await RequireRemoteAsync(git);
            try
            {
                foreach (var (status, failure) in new[] { ("401", GitFailure.Authentication), ("403", GitFailure.Permission) })
                {
                    var before = verifiedPeer.RequestCount;
                    verifiedPeer.SetMode(status);
                    var refusal = await git.InspectPullTargetAsync(remote);
                    Check(!refusal.Success && refusal.Failure == failure && verifiedPeer.RequestCount > before,
                        $"HTTP {status} returned {refusal.Failure}");
                    Check(refusal.StandardOutput.Length == 0, "remote output was returned");
                    await AssertPreservedAsync($"HTTP {status}", refusal);
                }
            }
            finally { verifiedPeer.SetMode("normal"); }
        });
        await Program.RunNamedCaseAsync("HTTPS Pull refuses an HTTPS redirect without changing the checkout", async () =>
        {
            // Both peers have valid fixture trust, so a TLS failure cannot mask a followed redirect.
            await using var redirectPeer = await HttpsServer.StartAsync(modeRoot, serverRoot, "verified", "redirect-target");
            //CSJ,variable=remote,リモート
            var remote = await RequireRemoteAsync(git);
            var before = verifiedPeer.RequestCount;
            verifiedPeer.SetMode($"redirect {new Uri(redirectPeer.Endpoint).Port}");
            try
            {
                var refusal = await git.InspectPullTargetAsync(remote);
                Check(!refusal.Success && refusal.Failure == GitFailure.RemoteRedirect &&
                    verifiedPeer.RequestCount > before && redirectPeer.RequestCount == 0,
                    $"redirect returned {refusal.Failure} or reached the replacement endpoint");
                Check(refusal.StandardOutput.Length == 0 && !refusal.StandardError.Contains(redirectPeer.Endpoint),
                    "redirect exposed peer-controlled output");
                await AssertPreservedAsync("HTTP 307", refusal);
            }
            finally { verifiedPeer.SetMode("normal"); }
        });
        await Program.RunNamedCaseAsync("HTTPS Pull cannot use a stopped loopback endpoint", async () =>
        {
            await wrongPeer.DisposeAsync();
            await ConfigureEndpointAsync(wrongPeer.Endpoint);
            try
            {
                //CSJ,variable=remote,リモート
                var remote = await RequireRemoteAsync(git);
                var refusal = await git.InspectPullTargetAsync(remote);
                Check(!refusal.Success && refusal.PullTarget is null, "a stopped fixture yielded a Pull target");
                await AssertPreservedAsync("stopped server", refusal);
            }
            finally { await ConfigureEndpointAsync(verifiedPeer.Endpoint); }
        });

        await Program.RunNamedCaseAsync("HTTPS Pull detects changed and deleted approved remote tips", async () =>
        {
            //CSJ,variable=approved,承認済み
            var approved = await RequireTargetAsync(git, target);
            await SetRemoteTipAsync(next);
            var moved = await git.PullTrustedAsync(approved);
            Check(!moved.Success, "moved remote tip accepted");
            await AssertPreservedAsync("moved tip", moved);
            await GitAsync(bare, "update-ref", "-d", "refs/heads/main");
            try
            {
                //CSJ,variable=deleted,削除済み
                var deleted = await git.PullTrustedAsync(approved);
                Check(!deleted.Success, "deleted remote tip accepted");
                await AssertPreservedAsync("deleted tip", deleted);
            }
            finally { await SetRemoteTipAsync(target); }
        });
        await Program.RunNamedCaseAsync("HTTPS Pull preserves checkout on HTTP failure, disconnect and deadline", async () =>
        {
            //CSJ,variable=approved,承認済み
            var approved = await RequireTargetAsync(git, target);
            //CSJ,variable=mode,モード
            foreach (var mode in new[] { "error", "disconnect" })
            {
                var before = verifiedPeer.RequestCount;
                verifiedPeer.SetMode(mode);
                var refusal = await git.PullTrustedAsync(approved);
                Check(!refusal.Success && verifiedPeer.RequestCount > before, $"{mode} did not reach the failure fixture");
                await AssertPreservedAsync(mode, refusal);
            }

            //CSJ,variable=timeout,タイムアウト
            var timeout = new GitRepository(checkout)
            {
                LoopbackPullCertificateAuthorityFile = authority,
                RemoteTimeout = TimeSpan.FromSeconds(4),
            };
            //CSJ,variable=beforeTimeout,変更前タイムアウト
            var beforeTimeout = verifiedPeer.RequestCount;
            verifiedPeer.SetMode("timeout");
            var timedOut = await timeout.PullTrustedAsync(approved);
            Check(!timedOut.Success && timedOut.Failure == GitFailure.Timeout && verifiedPeer.RequestCount > beforeTimeout,
                "deadline did not interrupt an actual HTTP request");
            await AssertPreservedAsync("timeout", timedOut);
            verifiedPeer.SetMode("normal");
        });
        await Program.RunNamedCaseAsync("HTTPS Pull checks the remote again after fetching the approved object", async () =>
        {
            //CSJ,variable=approved,承認済み
            var approved = await RequireTargetAsync(git, target);
            var entered = false;
            git.BeforePullExecutionRecheck = _ =>
            {
                SetRemoteTipAsync(next).GetAwaiter().GetResult();
                entered = true;
            };
            try
            {
                var refusal = await git.PullTrustedAsync(approved);
                Check(entered && !refusal.Success, "changed tip after HTTPS fetch was not rejected");
                await AssertPreservedAsync("moved tip after fetch", refusal);
            }
            finally
            {
                git.BeforePullExecutionRecheck = null;
                await SetRemoteTipAsync(target);
            }
        });
        await Program.RunNamedCaseAsync("Anonymous HTTPS Pull publishes only the approved commit and verifies checkout", async () =>
        {
            //CSJ,variable=approved,承認済み
            var approved = await RequireTargetAsync(git, target);
            var result = await git.PullTrustedAsync(approved);
            var after = await SnapshotAsync(checkout);
            Check(result.Success && after.Head == target && after.Status.Length == 0 &&
                await File.ReadAllTextAsync(Path.Combine(checkout, "project.ymmp")) == "accepted" &&
                after.IgnoredSha256 == baseline.IgnoredSha256 && !after.PendingEvidence,
                $"verified HTTPS checkout differs from the approval: {result.Failure} {result.StandardError}");
            await GitAsync(checkout, "diff-index", "--cached", "--quiet", target, "--");
            Check(!verifiedPeer.AccessLog.Contains("timeout POST", StringComparison.Ordinal), "unexpected request during timeout");
            Check(verifiedPeer.AccessLog.Contains("normal POST /fixture.git/git-upload-pack", StringComparison.Ordinal),
                "smart HTTPS upload-pack did not receive a real POST");
            Console.WriteLine($"HTTPS approved publication: HEAD {baseline.Head} -> {after.Head}; index SHA-256 {baseline.IndexSha256} -> {after.IndexSha256}; project SHA-256 {baseline.ProjectSha256} -> {after.ProjectSha256}; ignored SHA-256 {after.IgnoredSha256}; clean status; no recovery witness.");
        });

        async Task<string> CommitAsync(string content)
        {
            await File.WriteAllTextAsync(Path.Combine(source, "project.ymmp"), content);
            await GitAsync(source, "add", "--", ".gitignore", "project.ymmp");
            await GitAsync(source, "commit", "-m", content);
            return await GitAsync(source, "rev-parse", "HEAD");
        }

        Task<string> SetRemoteTipAsync(string objectId) => GitAsync(bare, "update-ref", "refs/heads/main", objectId);

        Task<string> ConfigureEndpointAsync(string endpoint) => GitAsync(checkout, "remote", "set-url", "origin", endpoint);

        async Task AssertPreservedAsync(string reason, GitResult failure)
        {
            Check(await SnapshotAsync(checkout) == baseline, $"HTTPS {reason} altered HEAD, index, files, status or the recovery witness");
            Console.WriteLine($"HTTPS {reason}: exit {failure.ExitCode} / {failure.Failure}; checkout unchanged.");
        }
    }

    private static async Task<GitPullRemote> RequireRemoteAsync(GitRepository git)
    {
        var result = await git.InspectPullRemoteAsync();
        Check(result.Success && result.PullRemote is not null, $"cannot inspect HTTPS fixture endpoint: {result.StandardError}");
        return result.PullRemote!;
    }

    private static async Task<GitPullTarget> RequireTargetAsync(GitRepository git, string target)
    {
        var result = await git.InspectPullTargetAsync(await RequireRemoteAsync(git));
        Check(result.Success && result.PullTarget?.TargetObjectId == target, $"unexpected HTTPS target: {result.StandardError}");
        return result.PullTarget!;
    }

    private static string WriteCertificateFixture(string root)
    {
        var now = DateTimeOffset.UtcNow;
        //CSJ,variable=authority,権限の根拠
        var authority = Path.Combine(root, "authority.pem");
        //CSJ,variable=verified,検証済み
        var verified = WritePeer("verified", "127.0.0.1");
        var wrong = WritePeer("wrong", "127.0.0.2");
        File.WriteAllText(authority, verified + "\n" + wrong + "\n");
        return authority;

        //CSJ,method=WritePeer,対向先を書き込む
        string WritePeer(string name, string ip)
        {
            //CSJ,variable=key,キー
            using var key = RSA.Create(2048);
            var leaf = new CertificateRequest($"CN={ip}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            //CSJ,variable=names,名前一覧
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(System.Net.IPAddress.Parse(ip));
            leaf.CertificateExtensions.Add(names.Build());
            // Pin the short-lived self-signed peer as its own fixture trust anchor;
            // the system's Schannel revocation policy remains enabled.
            leaf.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var certificate = leaf.CreateSelfSigned(now.AddMinutes(-1), now.AddMinutes(30));
            var pem = certificate.ExportCertificatePem();
            File.WriteAllText(Path.Combine(root, name + ".crt"), pem);
            File.WriteAllText(Path.Combine(root, name + ".key"), key.ExportPkcs8PrivateKeyPem());
            return pem;
        }
    }

    //CSJ,record=Snapshot,スナップショット
    private sealed record Snapshot(string Head, string IndexSha256, string IndexEntries, string ProjectSha256, string IgnoredSha256,
        string Status, bool PendingEvidence);

    private static async Task<Snapshot> SnapshotAsync(string directory) => new(
        await GitAsync(directory, "rev-parse", "HEAD"),
        Hash(Path.Combine(directory, ".git", "index")),
        await GitAsync(directory, "ls-files", "--stage", "-z"),
        Hash(Path.Combine(directory, "project.ymmp")),
        Hash(Path.Combine(directory, "untouched.bin")),
        await GitAsync(directory, "--no-optional-locks", "status", "--porcelain=v1"),
        GitPullOperationJournal.Exists(directory));

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static async Task<string> GitAsync(string directory, params string[] args)
    {
        //CSJ,variable=process,プロセス
        using var process = Start("git.exe", directory, args);
        var output = process.StandardOutput.ReadToEndAsync();
        //CSJ,variable=error,エラー
        var error = process.StandardError.ReadToEndAsync();
        //CSJ,variable=timeout,タイムアウト
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("Git fixture command did not exit.");
        }
        //CSJ,variable=diagnostic,診断
        var diagnostic = await error;
        Check(process.ExitCode == 0, $"Git fixture failed with {process.ExitCode}: {diagnostic}");
        return (await output).TrimEnd();
    }

    //CSJ,method=Start,開始する
    private static Process Start(string file, string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo(file)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        //CSJ,variable=name,名前
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_ATTR_NOSYSTEM"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_ALLOW_PROTOCOL"] = "file";
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        //CSJ,variable=argument,引数
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start an HTTPS fixture process.");
    }

    //CSJ,method=Check,確認する
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    //CSJ,class=FixtureEnvironment,テスト用データ環境
    private sealed class FixtureEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> variables = new();
        public FixtureEnvironment()
        {
            Set("GIT_CONFIG_GLOBAL", "NUL");
            Set("GIT_CONFIG_NOSYSTEM", "1");
            Set("GIT_TERMINAL_PROMPT", "0");
            Set("GIT_ALLOW_PROTOCOL", "file:https");
        }

        //CSJ,method=Set,設定する
        private void Set(string key, string value)
        {
            variables.Add(key, Environment.GetEnvironmentVariable(key));
            Environment.SetEnvironmentVariable(key, value);
        }

        //CSJ,method=Dispose,破棄する
        public void Dispose()
        {
            foreach (var (key, previous) in variables) Environment.SetEnvironmentVariable(key, previous);
        }
    }

    //CSJ,class=HttpsServer,HTTPSサーバー
    private sealed class HttpsServer(Process process, Task<string> errors, string mode, string accesses, int port) : IAsyncDisposable
    {
        //CSJ,field=disposal,破棄
        private Task? disposal;
        //CSJ,property=Endpoint,エンドポイント
        public string Endpoint => $"https://127.0.0.1:{port}/fixture.git";
        //CSJ,property=AccessLog,アクセスログ
        public string AccessLog => File.Exists(accesses) ? File.ReadAllText(accesses) : string.Empty;
        //CSJ,property=RequestCount,要求件数
        public int RequestCount => AccessLog.Count(c => c == '\n');
        //CSJ,method=SetMode,モードを設定する
        public void SetMode(string value) => File.WriteAllText(mode, value);

        //CSJ,method=StartAsync,開始する（非同期）
        public static async Task<HttpsServer> StartAsync(string root, string serverRoot, string certificateName, string? instanceName = null)
        {
            var script = Path.GetFullPath(Path.Combine("scripts", "r20-https-git-fixture.py"));
            Check(File.Exists(script), "Run HTTPS acceptance from the repository root.");
            //CSJ,variable=name,名前
            var name = instanceName ?? certificateName;
            //CSJ,variable=mode,モード
            var mode = Path.Combine(root, name + ".mode");
            var ready = Path.Combine(root, name + ".ready");
            var accesses = Path.Combine(root, name + ".access");
            File.WriteAllText(mode, "normal");
            var python = Environment.GetEnvironmentVariable("YMMPM_R20_PYTHON") ?? "python.exe";
            var child = Start(python, root, script, serverRoot, Path.Combine(root, certificateName + ".crt"), Path.Combine(root, certificateName + ".key"),
                mode, ready, accesses, "git.exe");
            var errors = child.StandardError.ReadToEndAsync();
            //CSJ,variable=elapsed,経過時間
            var elapsed = Stopwatch.StartNew();
            try
            {
                while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
                {
                    if (child.HasExited) throw new InvalidOperationException(await errors);
                    if (File.Exists(ready) && int.TryParse(await File.ReadAllTextAsync(ready), out var port))
                        return new HttpsServer(child, errors, mode, accesses, port);
                    await Task.Delay(50);
                }
                throw new TimeoutException("HTTPS fixture did not bind to loopback.");
            }
            catch
            {
                await StopAsync(child);
                throw;
            }
        }

        //CSJ,method=DisposeAsync,破棄する（非同期）
        public ValueTask DisposeAsync() => new(disposal ??= StopAndInspectAsync());

        private async Task StopAndInspectAsync()
        {
            var id = process.Id;
            await StopAsync(process);
            //CSJ,variable=diagnostic,診断
            var diagnostic = await errors;
            if (diagnostic.Contains("Traceback", StringComparison.Ordinal)) Console.WriteLine(diagnostic);
            Console.WriteLine($"HTTPS fixture PID {id} exited; listener on loopback port {port} closed.");
        }

        //CSJ,method=StopAsync,停止する（非同期）
        private static async Task StopAsync(Process child)
        {
            try
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                //CSJ,variable=timeout,タイムアウト
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await child.WaitForExitAsync(timeout.Token);
            }
            finally { child.Dispose(); }
        }
    }
}
