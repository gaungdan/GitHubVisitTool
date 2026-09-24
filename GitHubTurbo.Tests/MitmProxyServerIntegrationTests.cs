using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using GitHubTurbo.Proxy;

namespace GitHubTurbo.Tests;

/// <summary>
/// End-to-end MITM checks against a loopback fake upstream — no internet required.
/// Verifies local cert selection by SNI, fake-SNI outbound, and bidirectional byte piping.
/// </summary>
public sealed class MitmProxyServerIntegrationTests : IAsyncLifetime
{
    string _certDir = null!;
    CertificateManager _certManager = null!;
    EndpointCache _endpoints = null!;
    FakeUpstream _upstream = null!;
    MitmProxyServer _proxy = null!;
    int _proxyHttpsPort;
    int _proxyHttpPort;

    public async Task InitializeAsync()
    {
        _certDir = Path.Combine(Path.GetTempPath(), $"GitHubTurboMitmTests-{Guid.NewGuid():N}");
        _certManager = new CertificateManager(_certDir);
        _endpoints = new EndpointCache();

        _upstream = new FakeUpstream(_certManager.GetOrCreateServerCert("upstream.fake"));
        await _upstream.StartAsync();

        _proxyHttpsPort = GetFreePort();
        _proxyHttpPort = GetFreePort();
        _proxy = new MitmProxyServer(
            _certManager,
            _endpoints,
            listenHttpsPort: _proxyHttpsPort,
            listenHttpPort: _proxyHttpPort,
            upstreamHttpsPort: _upstream.Port);
        _proxy.Start(requireTrustedRoot: false);
    }

    public async Task DisposeAsync()
    {
        await _proxy.StopAsync();
        _proxy.Dispose();
        await _upstream.StopAsync();
        _certManager.Dispose();
        try { Directory.Delete(_certDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Https_PipesRequestThroughMitmToUpstream()
    {
        _endpoints.Seed("mitm.test", "127.0.0.1");

        var response = await SendHttpsAsync("mitm.test", "GET /hello HTTP/1.1\r\nHost: mitm.test\r\nConnection: close\r\n\r\n");

        Assert.True(response.Contains("HTTP/1.1 200 OK"),
            $"Upstream was not reached. Response: {response}\nFakeUpstream error: {_upstream.LastServerError}");
        Assert.Contains("upstream:mitm.test", response);
    }

    [Fact]
    public async Task Https_UsesLocalCaSignedCertSelectedBySni()
    {
        _endpoints.Seed("sni.test", "127.0.0.1");

        X509Certificate2? presented = null;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _proxyHttpsPort);
        await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
            (_, cert, _, _) =>
            {
                presented = cert as X509Certificate2;
                return true;
            });

        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "sni.test",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        });

        Assert.NotNull(presented);
        Assert.Contains("sni.test", presented!.Subject);

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_certManager.CaCert);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(chain.Build(presented), "Presented cert should chain to the GitHubTurbo Root CA.");
    }

    [Fact]
    public async Task Https_IpAddressStrategyIsReceivedAsEmptySni()
    {
        _endpoints.Seed("ip-sni.test", "127.0.0.1");

        var response = await SendHttpsAsync("ip-sni.test",
            "GET / HTTP/1.1\r\nHost: ip-sni.test\r\nConnection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 200 OK", response);
        // .NET omits IP literals from the ClientHello SNI extension, so the wire
        // representation of TlsSniMode.IpAddress is "no SNI".
        Assert.Equal(TlsSniMode.None, _upstream.LastAcceptedSniMode);
        Assert.Equal(TlsSniMode.IpAddress, _endpoints.GetPreferredSni("ip-sni.test"));
    }

    [Fact]
    public async Task Https_FallsBackToRealDomainWhenEmptySniRejected()
    {
        _endpoints.Seed("fallback.test", "127.0.0.1");
        _upstream.RejectEmptySni = true;
        try
        {
            var response = await SendHttpsAsync("fallback.test",
                "GET / HTTP/1.1\r\nHost: fallback.test\r\nConnection: close\r\n\r\n");

            Assert.Contains("HTTP/1.1 200 OK", response);
            Assert.Equal(TlsSniMode.RealDomain, _upstream.LastAcceptedSniMode);
            Assert.Equal(TlsSniMode.RealDomain, _endpoints.GetPreferredSni("fallback.test"));
        }
        finally
        {
            _upstream.RejectEmptySni = false;
        }
    }

    [Fact]
    public async Task Http_RedirectsToHttpsPreservingHostAndPath()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _proxyHttpPort);
        using var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes("GET /foo/bar?x=1 HTTP/1.1\r\nHost: example.com:8080\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);

        var buffer = new byte[2048];
        var read = await stream.ReadAsync(buffer);
        var response = Encoding.ASCII.GetString(buffer, 0, read);

        Assert.Contains("HTTP/1.1 301 Moved Permanently", response);
        Assert.Contains("Location: https://example.com/foo/bar?x=1", response);
    }

    [Fact]
    public async Task Https_Returns502WhenUpstreamUnreachable()
    {
        // Seed a non-routable address so outbound connect fails fast.
        _endpoints.Seed("dead.test", "127.0.0.1");
        await _upstream.StopAsync();

        var response = await SendHttpsAsync("dead.test",
            "GET / HTTP/1.1\r\nHost: dead.test\r\nConnection: close\r\n\r\n");

        Assert.Contains("502", response);

        await _upstream.StartAsync();
    }

    async Task<string> SendHttpsAsync(string sni, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _proxyHttpsPort);
        await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
            static (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = sni,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        });

        var bytes = Encoding.ASCII.GetBytes(request);
        await ssl.WriteAsync(bytes);
        await ssl.FlushAsync();

        var buffer = new byte[4096];
        var read = await ssl.ReadAsync(buffer);
        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Minimal TLS origin that records the outbound SNI and answers HTTP/1.1 200.
    /// Can optionally reject a specific SNI strategy so fallback logic can be observed.
    /// </summary>
    sealed class FakeUpstream
    {
        readonly X509Certificate2 _cert;
        readonly CancellationTokenSource _cts = new();
        TcpListener? _listener;
        Task? _loop;

        public int Port { get; private set; }

        public TlsSniMode? RejectSniMode { get; set; }

        public bool RejectEmptySni { get; set; }

        public TlsSniMode? LastAcceptedSniMode { get; private set; }

        public string? LastServerError { get; private set; }

        public FakeUpstream(X509Certificate2 cert) => _cert = cert;

        public Task StartAsync()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            _cts.Cancel();
            try { _listener?.Stop(); } catch { }
            if (_loop is not null)
            {
                try { await _loop; } catch { }
            }
        }

        async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch
                {
                    break;
                }

                _ = HandleAsync(client, cancellationToken);
            }
        }

        async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            try
            {
                using (client)
                {
                    await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                    string? sni = null;

                    try
                    {
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            ServerCertificateSelectionCallback = (_, name) =>
                            {
                                sni = name;
                                var mode = ClassifySni(name);
                                // Fail the handshake so the proxy falls through to the next SNI strategy.
                                if (RejectSniMode is not null && mode == RejectSniMode)
                                    throw new AuthenticationException($"SNI mode {mode} rejected by fake upstream.");
                                if (RejectEmptySni && mode == TlsSniMode.None)
                                    throw new AuthenticationException("Empty SNI rejected by fake upstream.");
                                LastAcceptedSniMode = mode;
                                return _cert;
                            },
                        }, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        LastServerError = ex.ToString();
                        return;
                    }

                    var buffer = new byte[2048];
                    var read = await ssl.ReadAsync(buffer, cancellationToken);
                    var request = Encoding.ASCII.GetString(buffer, 0, Math.Max(read, 0));
                    var host = MitmProxyServer.ExtractHost(request) ?? "unknown";
                    var body = $"upstream:{host}";
                    var bodyBytes = Encoding.UTF8.GetBytes(body);
                    var header = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
                    await ssl.WriteAsync(header, cancellationToken);
                    await ssl.WriteAsync(bodyBytes, cancellationToken);
                    await ssl.FlushAsync(cancellationToken);

                    // Graceful half-close so the proxy can read the response before RST.
                    try { await ssl.ShutdownAsync(); } catch { }
                    try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
                    try
                    {
                        while (await ssl.ReadAsync(buffer, cancellationToken) > 0) { }
                    }
                    catch { }
                }
            }
            catch
            {
                // Fake upstream is test-only; swallow accept/handshake races on shutdown.
            }
        }

        static TlsSniMode ClassifySni(string? sni)
        {
            if (string.IsNullOrEmpty(sni))
                return TlsSniMode.None;
            return IPAddress.TryParse(sni, out _) ? TlsSniMode.IpAddress : TlsSniMode.RealDomain;
        }
    }
}
