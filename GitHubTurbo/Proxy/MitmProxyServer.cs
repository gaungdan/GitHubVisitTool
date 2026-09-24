using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace GitHubTurbo.Proxy;

/// <summary>
/// Byte-stream MITM proxy.
///
/// Inbound: TLS terminated with a locally-trusted CA cert selected by SNI.
/// Outbound: TLS re-established against a preferred upstream IP with a fake SNI,
/// then plaintext bytes are piped both ways without parsing HTTP.
/// This preserves HTTP/2, WebSocket and large downloads automatically.
/// </summary>
public sealed class MitmProxyServer : IDisposable
{
    public const int HttpsPort = 443;
    public const int HttpPort = 80;

    readonly CertificateManager _certManager;
    readonly EndpointCache _endpointCache;
    readonly int _listenHttpsPort;
    readonly int _listenHttpPort;
    readonly int _upstreamHttpsPort;
    readonly CancellationTokenSource _cts = new();
    TcpListener? _httpsListener;
    TcpListener? _httpListener;
    Task? _httpsLoop;
    Task? _httpLoop;
    int _isRunning;

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    public EndpointCache Endpoints => _endpointCache;

    public MitmProxyServer(
        CertificateManager certManager,
        EndpointCache endpointCache,
        int listenHttpsPort = HttpsPort,
        int listenHttpPort = HttpPort,
        int upstreamHttpsPort = HttpsPort)
    {
        _certManager = certManager;
        _endpointCache = endpointCache;
        _listenHttpsPort = listenHttpsPort;
        _listenHttpPort = listenHttpPort;
        _upstreamHttpsPort = upstreamHttpsPort;
    }

    public void Start(bool requireTrustedRoot = true)
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) == 1)
            return;

        if (requireTrustedRoot && !_certManager.IsRootTrusted() && !_certManager.TrustRootCertificate())
            throw new InvalidOperationException("无法信任本地根证书，请以管理员身份运行。");

        _httpsListener = new TcpListener(IPAddress.Loopback, _listenHttpsPort);
        _httpsListener.Start(256);
        _httpListener = new TcpListener(IPAddress.Loopback, _listenHttpPort);
        _httpListener.Start(64);

        _httpsLoop = Task.Run(() => AcceptLoopAsync(_httpsListener, handleHttpAsync: false, _cts.Token));
        _httpLoop = Task.Run(() => AcceptLoopAsync(_httpListener, handleHttpAsync: true, _cts.Token));
        _ = AppLogger.InfoAsync($"MITM proxy listening on 127.0.0.1:{_listenHttpsPort}/{_listenHttpPort}.");
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _isRunning, 0) == 0)
            return;

        _cts.Cancel();
        try { _httpsListener?.Stop(); } catch { }
        try { _httpListener?.Stop(); } catch { }

        if (_httpsLoop is not null)
        {
            try { await _httpsLoop; } catch { }
        }
        if (_httpLoop is not null)
        {
            try { await _httpLoop; } catch { }
        }

        await AppLogger.InfoAsync("MITM proxy stopped.");
    }

    async Task AcceptLoopAsync(TcpListener listener, bool handleHttpAsync, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                continue;
            }

            _ = handleHttpAsync
                ? HandleHttpAsync(client, cancellationToken)
                : HandleHttpsAsync(client, cancellationToken);
        }
    }

    async Task HandleHttpsAsync(TcpClient client, CancellationToken cancellationToken)
    {
        string? domain = null;
        try
        {
            client.NoDelay = true;
            await using var clientStream = client.GetStream();
            await using var serverSsl = new SslStream(clientStream, leaveInnerStreamOpen: true);
            var serverOptions = new SslServerAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false,
                ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
                ServerCertificateSelectionCallback = (_, sni) =>
                {
                    domain = sni;
                    return _certManager.GetOrCreateServerCert(string.IsNullOrEmpty(sni) ? "github.com" : sni);
                },
            };

            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(8));
            await serverSsl.AuthenticateAsServerAsync(serverOptions, handshakeCts.Token);

            domain ??= "github.com";
            var negotiated = serverSsl.NegotiatedApplicationProtocol;

            var upstream = await ConnectUpstreamAsync(domain, negotiated, cancellationToken);
            if (upstream is null)
            {
                // Must reply inside the TLS stream — plaintext here desyncs the client.
                await WritePlainErrorAsync(serverSsl, "502 Bad Gateway", $"无法连接 {domain} 的上游节点。");
                return;
            }

            await using (upstream.Stream)
            {
                _endpointCache.RememberSni(domain, upstream.SniMode);
                await AppLogger.InfoAsync($"MITM {domain} → {upstream.Ip} (SNI={upstream.SniMode}, ALPN={negotiated})");
                await PipeBidirectionalAsync(serverSsl, upstream.Stream, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            if (domain is not null)
                _ = AppLogger.ErrorAsync($"MITM https error for {domain}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            client.Dispose();
        }
    }

    async Task HandleHttpAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            client.NoDelay = true;
            using var clientStream = client.GetStream();

            // Read just enough of the request to learn the Host, then 301 to HTTPS.
            var buffer = new byte[2048];
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCts.CancelAfter(TimeSpan.FromSeconds(5));
            var read = await clientStream.ReadAsync(buffer, readCts.Token);
            if (read <= 0)
                return;

            var requestText = Encoding.ASCII.GetString(buffer, 0, read);
            var host = ExtractHost(requestText);
            if (host is null)
            {
                await WritePlainErrorAsync(clientStream, "400 Bad Request", "Missing Host header.");
                return;
            }

            var path = ExtractPath(requestText);
            var redirect = $"HTTP/1.1 301 Moved Permanently\r\nLocation: https://{host}{path}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(redirect);
            await clientStream.WriteAsync(bytes, cancellationToken);
        }
        catch (Exception ex)
        {
            _ = AppLogger.ErrorAsync($"MITM http error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            client.Dispose();
        }
    }

    async Task<OutboundConnection?> ConnectUpstreamAsync(
        string domain,
        SslApplicationProtocol negotiated,
        CancellationToken cancellationToken)
    {
        var ips = await _endpointCache.GetIpsForConnectAsync(domain, cancellationToken);
        if (ips is null || ips.Count == 0)
            return null;

        var preferred = _endpointCache.GetPreferredSni(domain);
        var strategies = TlsSniHelper.PreferredOrder
            .OrderBy(m => m == preferred ? 0 : 1)
            .ToArray();

        foreach (var mode in strategies)
        {
            var attempt = await TryConnectAnyAsync(domain, ips, mode, negotiated, cancellationToken);
            if (attempt is not null)
                return attempt;
        }

        return null;
    }

    async Task<OutboundConnection?> TryConnectAnyAsync(
        string domain,
        IReadOnlyList<string> ips,
        TlsSniMode mode,
        SslApplicationProtocol negotiated,
        CancellationToken cancellationToken)
    {
        var tasks = ips.Take(3)
            .Select(ip => TryConnectOneAsync(domain, ip, mode, negotiated, cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        return results.FirstOrDefault(r => r is not null);
    }

    async Task<OutboundConnection?> TryConnectOneAsync(
        string domain,
        string ip,
        TlsSniMode mode,
        SslApplicationProtocol negotiated,
        CancellationToken cancellationToken)
    {
        Socket? socket = null;
        SslStream? ssl = null;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

            socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(IPAddress.Parse(ip), _upstreamHttpsPort, timeoutCts.Token);
            var networkStream = new NetworkStream(socket, ownsSocket: true);

            ssl = new SslStream(networkStream, leaveInnerStreamOpen: false);
            var targetHost = TlsSniHelper.CreateTargetHost(domain, ip, mode);

            var clientOptions = new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                // Fake SNI produces a name-mismatched cert by design; skip validation.
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            };
            if (negotiated != default)
                clientOptions.ApplicationProtocols = [negotiated];

            await ssl.AuthenticateAsClientAsync(clientOptions, timeoutCts.Token);
            return new OutboundConnection(ip, mode, ssl);
        }
        catch (Exception ex)
        {
            _ = AppLogger.ErrorAsync($"Outbound {domain} via {ip} SNI={mode} failed: {ex.GetType().Name}: {ex.Message}");
            ssl?.Dispose();
            socket?.Dispose();
            return null;
        }
    }

    static async Task PipeBidirectionalAsync(Stream client, Stream upstream, CancellationToken cancellationToken)
    {
        var c2u = CopyOneWayAsync(client, upstream, cancellationToken);
        var u2c = CopyOneWayAsync(upstream, client, cancellationToken);
        await Task.WhenAny(c2u, u2c);

        // Let the trailing direction flush remaining bytes before the socket is torn down.
        var other = ReferenceEquals(c2u, Task.CompletedTask) || c2u.IsCompleted ? u2c : c2u;
        if (!other.IsCompleted)
        {
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            grace.CancelAfter(TimeSpan.FromMilliseconds(250));
            try { await other; } catch { }
        }
    }

    static async Task CopyOneWayAsync(Stream from, Stream to, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await from.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read <= 0)
                    break;
                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await to.FlushAsync(cancellationToken);
            }
        }
        catch
        {
            // Connection reset / half-close is normal at the end of a stream.
        }
        finally
        {
            try
            {
                if (to is SslStream ssl)
                    await ssl.ShutdownAsync();
                else if (to is NetworkStream ns)
                    ns.Socket.Shutdown(SocketShutdown.Send);
            }
            catch { }
        }
    }

    static async Task WritePlainErrorAsync(Stream stream, string status, string message)
    {
        try
        {
            var body = Encoding.UTF8.GetBytes(message);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(body);
        }
        catch { }
    }

    public static string? ExtractHost(string requestText)
    {
        foreach (var line in requestText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                return trimmed[5..].Trim().Split(':')[0];
        }
        return null;
    }

    public static string ExtractPath(string requestText)
    {
        var firstLine = requestText.Split('\r', '\n')[0];
        var parts = firstLine.Split(' ');
        return parts.Length >= 2 && parts[1].StartsWith('/') ? parts[1] : "/";
    }

    public void Dispose() => _cts.Dispose();

    sealed record OutboundConnection(string Ip, TlsSniMode SniMode, SslStream Stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Stream.DisposeAsync();
    }
}
