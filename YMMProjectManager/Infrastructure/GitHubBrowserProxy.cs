using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace YMMProjectManager.Infrastructure.Git;

/// <summary>A private TLS tunnel for the two HTTPS origins used by the reviewed GCM GitHub flow.</summary>
internal sealed class GitHubBrowserProxy : IAsyncDisposable
{
    private const int HeaderLimit = 8192;
    private const int ConnectionLimit = 32;
    private readonly CancellationTokenSource shutdown = new();
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly Task serving;
    private readonly Func<string, CancellationToken, Task<TcpClient>> connect;
    private int denied;
    private int opened;

    public GitHubBrowserProxy() : this(ConnectAsync) { }

    internal GitHubBrowserProxy(Func<string, CancellationToken, Task<TcpClient>> connect)
    {
        this.connect = connect;
        listener.ExclusiveAddressUse = true;
        listener.Start();
        Endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        serving = ServeAsync();
    }

    public Uri Endpoint { get; }
    public bool Denied => Volatile.Read(ref denied) != 0;
    public bool Opened => Volatile.Read(ref opened) != 0;

    // Keep the port reserved until GCM exits, including if child termination fails.
    public void StopForwarding() => shutdown.Cancel();

    private async Task ServeAsync()
    {
        var connections = new List<Task>(ConnectionLimit);
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(shutdown.Token).ConfigureAwait(false);
                if (connections.Count == ConnectionLimit)
                {
                    Interlocked.Exchange(ref denied, 1);
                    client.Dispose();
                    continue;
                }

                connections.Add(HandleAsync(client, shutdown.Token));
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (SocketException)
        {
            Interlocked.Exchange(ref denied, 1);
            StopForwarding();
        }
        finally
        {
            await Task.WhenAll(connections).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellation)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var host = await ReadAllowedHostAsync(stream, cancellation).ConfigureAwait(false);
                if (host is null)
                {
                    Interlocked.Exchange(ref denied, 1);
                    // Do not request proxy authentication, since GCM can offer Windows default credentials.
                    await stream.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), cancellation)
                        .ConfigureAwait(false);
                    return;
                }

                using var destination = await connect(host, cancellation).ConfigureAwait(false);
                var upstream = destination.GetStream();
                Interlocked.Exchange(ref opened, 1);
                await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), cancellation).ConfigureAwait(false);

                using var tunnel = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                var sending = stream.CopyToAsync(upstream, tunnel.Token);
                var receiving = upstream.CopyToAsync(stream, tunnel.Token);
                await Task.WhenAny(sending, receiving).ConfigureAwait(false);
                await tunnel.CancelAsync().ConfigureAwait(false);
                await Task.WhenAll(sending, receiving).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
        }
    }

    private static async Task<TcpClient> ConnectAsync(string host, CancellationToken cancellation)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, 443, cancellation).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal static async Task<string?> ReadAllowedHostAsync(Stream stream, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var header = new byte[HeaderLimit];
        var length = 0;
        try
        {
            while (length < header.Length)
            {
                var read = await stream.ReadAsync(header.AsMemory(length), deadline.Token).ConfigureAwait(false);
                if (read == 0) return null;
                length += read;
                var end = header.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
                if (end < 0) continue;
                if (end + 4 != length) return null; // CONNECT has no body.
                return ReadAllowedHost(header.AsSpan(0, end));
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
        return null;
    }

    private static string? ReadAllowedHost(ReadOnlySpan<byte> header)
    {
        var firstLine = header.IndexOf("\r\n"u8);
        var request = firstLine < 0 ? header : header[..firstLine];
        var host = Encoding.ASCII.GetString(request) switch
        {
            "CONNECT github.com:443 HTTP/1.1" => "github.com",
            "CONNECT api.github.com:443 HTTP/1.1" => "api.github.com",
            _ => null,
        };
        if (host is null || firstLine < 0) return null;

        var hostSeen = false;
        var fields = Encoding.ASCII.GetString(header[(firstLine + 2)..]).Split("\r\n", StringSplitOptions.None);
        foreach (var field in fields)
        {
            var separator = field.IndexOf(':');
            if (separator <= 0 || field.Contains('\r') || field.Contains('\n') || field[0] is ' ' or '\t') return null;
            var name = field[..separator];
            if (name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                return null;
            if (!name.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (hostSeen || field[(separator + 1)..].Trim() != host + ":443") return null;
            hostSeen = true;
        }

        return hostSeen ? host : null;
    }

    public async ValueTask DisposeAsync()
    {
        StopForwarding();
        listener.Stop();
        try { await serving.ConfigureAwait(false); }
        finally { shutdown.Dispose(); }
    }
}
