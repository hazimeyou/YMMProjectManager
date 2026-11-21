using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using YMMProjectManager.Infrastructure.Git;

internal static class GitHubBrowserLoginTests
{
    internal static async Task RunAsync(string workRoot)
    {
        await Program.RunNamedCaseAsync("R24 browser helper uses fixed GitHub arguments and a fresh process profile",
            () => VerifyProfileAsync(workRoot));
        await Program.RunNamedCaseAsync("R24 browser helper installation ACL rejects ordinary write grants",
            VerifyInstallationAclAsync);
        await Program.RunNamedCaseAsync("R24 browser HTTPS relay refuses other hosts, routes and proxy credentials",
            VerifyConnectRulesAsync);
        await Program.RunNamedCaseAsync("R24 browser HTTPS relay binds exclusively and refuses without an auth challenge",
            VerifyRelayAsync);
        await Program.RunNamedCaseAsync("R24 approved browser tunnel relays data and permits a verified helper success",
            VerifyApprovedRelayAsync);
        await Program.RunNamedCaseAsync("R24 browser child has no credentials on output and cannot claim success without its relay",
            VerifyExitAsync);
        await Program.RunNamedCaseAsync("R24 browser cancel and timeout after launch wait for the contained child",
            VerifyTerminationAsync);
        await Program.RunNamedCaseAsync("R24 browser cancellation before launch propagates without starting the helper",
            VerifyPreCancellationAsync);
    }

    private static Task VerifyProfileAsync(string root)
    {
        var proxy = new Uri("http://127.0.0.1:32119");
        var path = Path.Combine(root, "isolated-git");
        var oldTrace = Environment.GetEnvironmentVariable("GCM_TRACE_SECRETS");
        var oldBypass = Environment.GetEnvironmentVariable("NO_PROXY");
        var oldConfig = Environment.GetEnvironmentVariable("GIT_CONFIG_KEY_33");
        try
        {
            Environment.SetEnvironmentVariable("GCM_TRACE_SECRETS", "fixture");
            Environment.SetEnvironmentVariable("NO_PROXY", "*");
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_33", "http.sslVerify");
            var env = GitRepository.BuildBrowserGcmEnvironment(path, path, proxy);
            Require(!env.ContainsKey("GCM_TRACE_SECRETS") && !env.ContainsKey("NO_PROXY") &&
                !env.ContainsKey("GIT_CONFIG_KEY_33"), "inherited override escaped browser login profile");
            Require(env["GIT_CONFIG_NOSYSTEM"] == "1" && env["GIT_CONFIG_GLOBAL"] == env["GIT_CONFIG_SYSTEM"] &&
                !File.Exists(env["GIT_CONFIG_GLOBAL"]), "config isolation");
            Require(env["GIT_EXEC_PATH"] == path && env["GIT_CEILING_DIRECTORIES"] == root, "unexpected Git resolution profile");
            Require(env["GCM_CREDENTIAL_STORE"] == "wincredman" && env["GIT_TERMINAL_PROMPT"] == "0" &&
                env["GCM_INTERACTIVE"] == "1" && env["GCM_GUI_PROMPT"] == "0", "unexpected interactive or credential contract");
            var configuredProxy = new Uri(env["GIT_CONFIG_VALUE_0"]);
            Require(configuredProxy.Host == proxy.Host && configuredProxy.Port == proxy.Port &&
                configuredProxy.UserInfo.StartsWith("local:", StringComparison.Ordinal), "unexpected proxy authority");
            Require(env["GIT_CONFIG_KEY_1"] == "http.sslVerify" && env["GIT_CONFIG_VALUE_1"] == "true", "TLS verification must be enforced");
            Require(GitRepository.BrowserGcmArguments(false) == "github login --url https://github.com --browser" &&
                GitRepository.BrowserGcmArguments(true) == "github login --url https://github.com --browser --force", "browser argument contract");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GCM_TRACE_SECRETS", oldTrace);
            Environment.SetEnvironmentVariable("NO_PROXY", oldBypass);
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_33", oldConfig);
        }

        return Task.CompletedTask;
    }

    private static Task VerifyInstallationAclAsync()
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var access = new FileSecurity();
        access.SetOwner(administrators);
        access.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        access.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        Require(GitRepository.IsProtectedInstallationEntry(access), "normal protected executable ACL rejected");
        access.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Write, AccessControlType.Allow));
        Require(!GitRepository.IsProtectedInstallationEntry(access), "ordinary write grant was accepted");
        var missingDacl = new FileSecurity();
        missingDacl.SetSecurityDescriptorSddlForm("O:SYD:NO_ACCESS_CONTROL");
        Require(!GitRepository.IsProtectedInstallationEntry(missingDacl), "a null DACL grants access to everyone");
        return Task.CompletedTask;
    }

    private static async Task VerifyConnectRulesAsync()
    {
        foreach (var origin in new[] { "github.com", "api.github.com" })
            Require(await ParseAsync($"CONNECT {origin}:443 HTTP/1.1\r\nHost: {origin}:443\r\n\r\n") == origin, "approved CONNECT rejected");
        foreach (var unsafeRequest in new[]
        {
            "CONNECT localhost:443 HTTP/1.1\r\nHost: localhost:443\r\n\r\n",
            "CONNECT 127.0.0.1:443 HTTP/1.1\r\nHost: 127.0.0.1:443\r\n\r\n",
            "CONNECT [::1]:443 HTTP/1.1\r\nHost: [::1]:443\r\n\r\n",
            "CONNECT github.com:444 HTTP/1.1\r\nHost: github.com:444\r\n\r\n",
            "CONNECT github.com.invalid:443 HTTP/1.1\r\nHost: github.com.invalid:443\r\n\r\n",
            "GET http://github.com/ HTTP/1.1\r\nHost: github.com\r\n\r\n",
            "CONNECT github.com:443 HTTP/1.1\r\nHost: other.invalid:443\r\n\r\n",
            "CONNECT github.com:443 HTTP/1.1\r\nHost: github.com:443\r\nHost: other.invalid:443\r\n\r\n",
            "CONNECT github.com:443 HTTP/1.1\r\nHost: github.com:443\r\nProxy-Authorization: synthetic\r\n\r\n",
            "CONNECT github.com:443 HTTP/1.1\r\nHost: github.com:443\r\n\r\nbody",
            "CONNECT github.com:443 HTTP/1.1\r\nHost: github.com:443\r\nX-Large: " + new string('a', 8192) + "\r\n\r\n",
        })
            Require(await ParseAsync(unsafeRequest) is null, "unsupported request was accepted");

        static async Task<string?> ParseAsync(string header)
        {
            using var stream = new MemoryStream(Encoding.ASCII.GetBytes(header));
            return await GitHubBrowserProxy.ReadAllowedHostAsync(stream, CancellationToken.None);
        }
    }

    private static async Task VerifyRelayAsync()
    {
        await using var proxy = new GitHubBrowserProxy();
        var port = proxy.Endpoint.Port;
        Require(proxy.Endpoint.Host == "127.0.0.1", "proxy listener escaped loopback");
        using var competing = new TcpListener(IPAddress.Loopback, port);
        try
        {
            competing.Start();
            throw new InvalidOperationException("proxy socket was not reserved exclusively");
        }
        catch (SocketException) { }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
        await client.GetStream().WriteAsync("CONNECT localhost:443 HTTP/1.1\r\nHost: localhost:443\r\n\r\n"u8.ToArray(), deadline.Token);
        using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
        var response = await reader.ReadToEndAsync(deadline.Token);
        Require(response.StartsWith("HTTP/1.1 403", StringComparison.Ordinal) &&
            !response.Contains("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) && proxy.Denied && !proxy.Opened,
            "relay refusal must not challenge or open an upstream connection");
    }

    private static async Task VerifyExitAsync()
    {
        await using var proxy = new GitHubBrowserProxy();
        var work = Environment.SystemDirectory;
        var env = GitRepository.BuildBrowserGcmEnvironment(work, work, proxy.Endpoint);
        var fixture = Path.Combine(work, "cmd.exe");

        using (var child = GitHubLoginProcess.Start(fixture, "/d /c \"echo synthetic-output & echo synthetic-error 1>&2 & exit /b 13\"", work, env))
        {
            await child.Exited.WaitAsync(TimeSpan.FromSeconds(5));
            Require(child.HasExited && child.ReadExitCode() == 13, "native child did not exit with the expected result");
            var result = await GitRepository.FinishBrowserLoginAsync(child, proxy, TimeSpan.FromSeconds(5), CancellationToken.None);
            Require(result.Failure == GitFailure.Authentication && result.StandardOutput.Length == 0 &&
                !result.StandardError.Contains("synthetic-", StringComparison.Ordinal), "raw child output escaped");
        }

        using var unverified = GitHubLoginProcess.Start(fixture, "/d /c \"exit /b 0\"", work, env);
        await unverified.Exited.WaitAsync(TimeSpan.FromSeconds(5));
        var success = await GitRepository.FinishBrowserLoginAsync(unverified, proxy, TimeSpan.FromSeconds(5), CancellationToken.None);
        Require(success.Failure == GitFailure.OutcomeUnknown, "exit zero without proxy evidence claimed a login");
    }

    private static async Task VerifyApprovedRelayAsync()
    {
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        destination.Start();
        var destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;
        var connected = false;
        await using var proxy = new GitHubBrowserProxy(async (host, token) =>
        {
            Require(host == "api.github.com", "proxy connected an unapproved test endpoint");
            connected = true;
            var endpoint = new TcpClient(AddressFamily.InterNetwork);
            await endpoint.ConnectAsync(IPAddress.Loopback, destinationPort, token);
            return endpoint;
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, proxy.Endpoint.Port, deadline.Token);
        var stream = client.GetStream();
        await stream.WriteAsync("CONNECT api.github.com:443 HTTP/1.1\r\nHost: api.github.com:443\r\n\r\n"u8.ToArray(), deadline.Token);
        using var server = await destination.AcceptTcpClientAsync(deadline.Token);
        var header = "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray();
        var response = new byte[header.Length];
        await stream.ReadExactlyAsync(response, deadline.Token);
        Require(response.AsSpan().SequenceEqual(header) && connected && proxy.Opened && !proxy.Denied, "approved tunnel not established");
        var syntheticRequest = "synthetic-request"u8.ToArray();
        await stream.WriteAsync(syntheticRequest, deadline.Token);
        var read = new byte[syntheticRequest.Length];
        await server.GetStream().ReadExactlyAsync(read, deadline.Token);
        Require(read.AsSpan().SequenceEqual(syntheticRequest), "proxy did not forward the request bytes");
        await server.GetStream().WriteAsync(syntheticRequest, deadline.Token);
        await stream.ReadExactlyAsync(read, deadline.Token);
        Require(read.AsSpan().SequenceEqual(syntheticRequest), "proxy did not forward the response bytes");

        var work = Environment.SystemDirectory;
        var env = GitRepository.BuildBrowserGcmEnvironment(work, work, proxy.Endpoint);
        using var helper = GitHubLoginProcess.Start(Path.Combine(work, "cmd.exe"), "/d /c \"exit /b 0\"", work, env);
        var outcome = await GitRepository.FinishBrowserLoginAsync(helper, proxy, TimeSpan.FromSeconds(5), deadline.Token);
        Require(outcome.Success, "verified synthetic helper did not report success");
    }

    private static async Task VerifyTerminationAsync()
    {
        foreach (var cancelled in new[] { true, false })
        {
            await using var proxy = new GitHubBrowserProxy();
            var work = Environment.SystemDirectory;
            var env = GitRepository.BuildBrowserGcmEnvironment(work, work, proxy.Endpoint);
            using var child = GitHubLoginProcess.Start(Path.Combine(work, "ping.exe"), "-n 31 127.0.0.1", work, env);
            using var request = new CancellationTokenSource();
            if (cancelled) request.Cancel();
            var result = await GitRepository.FinishBrowserLoginAsync(child, proxy,
                cancelled ? TimeSpan.FromSeconds(5) : TimeSpan.Zero, request.Token).WaitAsync(TimeSpan.FromSeconds(8));
            Require(result.Failure == (cancelled ? GitFailure.OutcomeUnknown : GitFailure.Timeout) && child.HasExited,
                "caller returned before child exit or reported a post-launch interruption as safe");
            using var contest = new TcpListener(IPAddress.Loopback, proxy.Endpoint.Port);
            try
            {
                contest.Start();
                throw new InvalidOperationException("proxy reservation was released during child termination");
            }
            catch (SocketException) { }
        }
    }

    private static async Task VerifyPreCancellationAsync()
    {
        using var request = new CancellationTokenSource();
        request.Cancel();
        try
        {
            await GitRepository.SignInGitHubInBrowserAsync(false, request.Token);
            throw new InvalidOperationException("pre-cancelled browser login did not propagate cancellation");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
