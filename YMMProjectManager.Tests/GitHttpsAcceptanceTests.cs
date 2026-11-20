using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using YMMProjectManager.Infrastructure.Git;

internal static class GitHttpsAcceptanceTests
{
    public static async Task RunAsync(string workRoot)
    {
        var modeRoot = Directory.CreateDirectory(Path.Combine(workRoot, "https-pull")).FullName;
        var serverRoot = Directory.CreateDirectory(Path.Combine(modeRoot, "server")).FullName;
        var bare = Path.Combine(serverRoot, "fixture.git");
        var source = Path.Combine(modeRoot, "source");
        var checkout = Path.Combine(modeRoot, "checkout");
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
        var baseline = await SnapshotAsync(checkout);
        Check(baseline.Head == old, "unexpected fixture baseline");

        var authority = WriteCertificateFixture(modeRoot);
        await using var verifiedPeer = await HttpsServer.StartAsync(modeRoot, serverRoot, "verified");
        await using var wrongPeer = await HttpsServer.StartAsync(modeRoot, serverRoot, "wrong");
        await ConfigureEndpointAsync(verifiedPeer.Endpoint);
        var production = new GitRepository(checkout);
        var git = new GitRepository(checkout) { LoopbackPullCertificateAuthorityFile = authority };

        await Program.RunNamedCaseAsync("HTTPS Pull rejects an untrusted certificate with stock Git trust", async () =>
        {
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
                var remote = await RequireRemoteAsync(git);
                var refusal = await git.InspectPullTargetAsync(remote);
                Check(!refusal.Success && refusal.PullTarget is null && wrongPeer.RequestCount == 0,
                    "IP mismatch reached the HTTP peer despite enabled certificate verification");
                await AssertPreservedAsync("certificate IP mismatch", refusal);
            }
            finally { await ConfigureEndpointAsync(verifiedPeer.Endpoint); }
        });
        await Program.RunNamedCaseAsync("HTTPS Pull cannot use a stopped loopback endpoint", async () =>
        {
            await wrongPeer.DisposeAsync();
            await ConfigureEndpointAsync(wrongPeer.Endpoint);
            try
            {
                var remote = await RequireRemoteAsync(git);
                var refusal = await git.InspectPullTargetAsync(remote);
                Check(!refusal.Success && refusal.PullTarget is null, "a stopped fixture yielded a Pull target");
                await AssertPreservedAsync("stopped server", refusal);
            }
            finally { await ConfigureEndpointAsync(verifiedPeer.Endpoint); }
        });

        await Program.RunNamedCaseAsync("HTTPS Pull detects changed and deleted approved remote tips", async () =>
        {
            var approved = await RequireTargetAsync(git, target);
            await SetRemoteTipAsync(next);
            var moved = await git.PullTrustedAsync(approved);
            Check(!moved.Success, "moved remote tip accepted");
            await AssertPreservedAsync("moved tip", moved);
            await GitAsync(bare, "update-ref", "-d", "refs/heads/main");
            try
            {
                var deleted = await git.PullTrustedAsync(approved);
                Check(!deleted.Success, "deleted remote tip accepted");
                await AssertPreservedAsync("deleted tip", deleted);
            }
            finally { await SetRemoteTipAsync(target); }
        });
        await Program.RunNamedCaseAsync("HTTPS Pull preserves checkout on HTTP failure, disconnect and deadline", async () =>
        {
            var approved = await RequireTargetAsync(git, target);
            foreach (var mode in new[] { "error", "disconnect" })
            {
                var before = verifiedPeer.RequestCount;
                verifiedPeer.SetMode(mode);
                var refusal = await git.PullTrustedAsync(approved);
                Check(!refusal.Success && verifiedPeer.RequestCount > before, $"{mode} did not reach the failure fixture");
                await AssertPreservedAsync(mode, refusal);
            }

            var timeout = new GitRepository(checkout)
            {
                LoopbackPullCertificateAuthorityFile = authority,
                RemoteTimeout = TimeSpan.FromSeconds(4),
            };
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
        var authority = Path.Combine(root, "authority.pem");
        var verified = WritePeer("verified", "127.0.0.1");
        var wrong = WritePeer("wrong", "127.0.0.2");
        File.WriteAllText(authority, verified + "\n" + wrong + "\n");
        return authority;

        string WritePeer(string name, string ip)
        {
            using var key = RSA.Create(2048);
            var leaf = new CertificateRequest($"CN={ip}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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
        using var process = Start("git.exe", directory, args);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("Git fixture command did not exit.");
        }
        var diagnostic = await error;
        Check(process.ExitCode == 0, $"Git fixture failed with {process.ExitCode}: {diagnostic}");
        return (await output).TrimEnd();
    }

    private static Process Start(string file, string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo(file)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start an HTTPS fixture process.");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

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

        private void Set(string key, string value)
        {
            variables.Add(key, Environment.GetEnvironmentVariable(key));
            Environment.SetEnvironmentVariable(key, value);
        }

        public void Dispose()
        {
            foreach (var (key, previous) in variables) Environment.SetEnvironmentVariable(key, previous);
        }
    }

    private sealed class HttpsServer(Process process, Task<string> errors, string mode, string accesses, int port) : IAsyncDisposable
    {
        private Task? disposal;
        public string Endpoint => $"https://127.0.0.1:{port}/fixture.git";
        public string AccessLog => File.Exists(accesses) ? File.ReadAllText(accesses) : string.Empty;
        public int RequestCount => AccessLog.Count(c => c == '\n');
        public void SetMode(string value) => File.WriteAllText(mode, value);

        public static async Task<HttpsServer> StartAsync(string root, string serverRoot, string name)
        {
            var script = Path.GetFullPath(Path.Combine("scripts", "r20-https-git-fixture.py"));
            Check(File.Exists(script), "Run HTTPS acceptance from the repository root.");
            var mode = Path.Combine(root, name + ".mode");
            var ready = Path.Combine(root, name + ".ready");
            var accesses = Path.Combine(root, name + ".access");
            File.WriteAllText(mode, "normal");
            var python = Environment.GetEnvironmentVariable("YMMPM_R20_PYTHON") ?? "python.exe";
            var child = Start(python, root, script, serverRoot, Path.Combine(root, name + ".crt"), Path.Combine(root, name + ".key"),
                mode, ready, accesses, "git.exe");
            var errors = child.StandardError.ReadToEndAsync();
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

        public ValueTask DisposeAsync() => new(disposal ??= StopAndInspectAsync());

        private async Task StopAndInspectAsync()
        {
            var id = process.Id;
            await StopAsync(process);
            var diagnostic = await errors;
            if (diagnostic.Contains("Traceback", StringComparison.Ordinal)) Console.WriteLine(diagnostic);
            Console.WriteLine($"HTTPS fixture PID {id} exited; listener on loopback port {port} closed.");
        }

        private static async Task StopAsync(Process child)
        {
            try
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await child.WaitForExitAsync(timeout.Token);
            }
            finally { child.Dispose(); }
        }
    }
}
