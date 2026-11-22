using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace YMMProjectManager.Infrastructure.Git;

/// <summary>A private TLS tunnel for the two HTTPS origins used by the reviewed GCM GitHub flow.</summary>
internal sealed class GitHubBrowserProxy : IAsyncDisposable
{
    private const int HeaderLimit = 8192;
    //CSJ,field=ConnectionLimit,接続上限
    private const int ConnectionLimit = 32;
    private readonly CancellationTokenSource shutdown = new();
    //CSJ,field=listener,リスナー
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly Task serving;
    private readonly Func<string, CancellationToken, Task<TcpClient>> connect;
    private int denied;
    //CSJ,field=opened,表示済み
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

    //CSJ,property=Endpoint,エンドポイント
    public Uri Endpoint { get; }
    //CSJ,property=Denied,拒否済み
    public bool Denied => Volatile.Read(ref denied) != 0;
    //CSJ,property=Opened,表示済み
    public bool Opened => Volatile.Read(ref opened) != 0;

    // Keep the port reserved until GCM exits, including if child termination fails.
    //CSJ,method=StopForwarding,転送を停止する
    public void StopForwarding() => shutdown.Cancel();

    private async Task ServeAsync()
    {
        var connections = new List<Task>(ConnectionLimit);
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                //CSJ,variable=client,クライアント
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

    //CSJ,method=HandleAsync,処理する（非同期）
    private async Task HandleAsync(TcpClient client, CancellationToken cancellation)
    {
        using (client)
        {
            try
            {
                //CSJ,variable=stream,ストリーム
                var stream = client.GetStream();
                //CSJ,variable=host,ホスト
                var host = await ReadAllowedHostAsync(stream, cancellation).ConfigureAwait(false);
                if (host is null)
                {
                    Interlocked.Exchange(ref denied, 1);
                    // Do not request proxy authentication, since GCM can offer Windows default credentials.
                    await stream.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), cancellation)
                        .ConfigureAwait(false);
                    return;
                }

                //CSJ,variable=destination,格納先
                using var destination = await connect(host, cancellation).ConfigureAwait(false);
                //CSJ,variable=upstream,上流
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
        //CSJ,variable=client,クライアント
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

    //CSJ,method=ReadAllowedHostAsync,許可済みホストを読み取る（非同期）
    internal static async Task<string?> ReadAllowedHostAsync(Stream stream, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var header = new byte[HeaderLimit];
        //CSJ,variable=length,長さ
        var length = 0;
        try
        {
            while (length < header.Length)
            {
                //CSJ,variable=read,読み取り
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

    //CSJ,method=ReadAllowedHost,許可済みホストを読み取る
    private static string? ReadAllowedHost(ReadOnlySpan<byte> header)
    {
        //CSJ,variable=firstLine,最初行
        var firstLine = header.IndexOf("\r\n"u8);
        //CSJ,variable=request,要求
        var request = firstLine < 0 ? header : header[..firstLine];
        //CSJ,variable=host,ホスト
        var host = Encoding.ASCII.GetString(request) switch
        {
            "CONNECT github.com:443 HTTP/1.1" => "github.com",
            "CONNECT api.github.com:443 HTTP/1.1" => "api.github.com",
            _ => null,
        };
        if (host is null || firstLine < 0) return null;

        var hostSeen = false;
        //CSJ,variable=fields,フィールド一覧
        var fields = Encoding.ASCII.GetString(header[(firstLine + 2)..]).Split("\r\n", StringSplitOptions.None);
        //CSJ,variable=field,フィールド
        foreach (var field in fields)
        {
            //CSJ,variable=separator,区切り
            var separator = field.IndexOf(':');
            if (separator <= 0 || field.Contains('\r') || field.Contains('\n') || field[0] is ' ' or '\t') return null;
            //CSJ,variable=name,名前
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

    //CSJ,method=DisposeAsync,破棄する（非同期）
    public async ValueTask DisposeAsync()
    {
        StopForwarding();
        listener.Stop();
        try { await serving.ConfigureAwait(false); }
        finally { shutdown.Dispose(); }
    }
}
