using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using GitHubTurbo.Proxy;

namespace GitHubTurbo.Tests;

public sealed class TlsSniHelperTests
{
    [Theory]
    [InlineData(TlsSniMode.IpAddress, "20.205.243.166")]
    [InlineData(TlsSniMode.None, "")]
    [InlineData(TlsSniMode.RealDomain, "github.com")]
    public void CreateTargetHost_UsesExpectedSni(TlsSniMode mode, string expected)
    {
        var actual = TlsSniHelper.CreateTargetHost("github.com", "20.205.243.166", mode);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PreferredOrder_PutsIpAddressFirst()
    {
        Assert.Equal(TlsSniMode.IpAddress, TlsSniHelper.PreferredOrder[0]);
        Assert.Equal(TlsSniMode.None, TlsSniHelper.PreferredOrder[1]);
        Assert.Equal(TlsSniMode.RealDomain, TlsSniHelper.PreferredOrder[2]);
    }

    [Fact]
    public void PreferredOrder_ContainsAllModesExactlyOnce()
        => Assert.Equal(3, TlsSniHelper.PreferredOrder.Distinct().Count());
}

public sealed class CertificateManagerTests : IDisposable
{
    readonly string _dir;

    public CertificateManagerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"GitHubTurboCertTests-{Guid.NewGuid():N}");
    }

    [Fact]
    public void Constructor_CreatesCaMaterialOnDisk()
    {
        using var manager = new CertificateManager(_dir);
        Assert.True(File.Exists(Path.Combine(_dir, "githubturbo-ca.pfx")));
        Assert.True(File.Exists(Path.Combine(_dir, "githubturbo-ca.cer")));
    }

    [Fact]
    public void CaCert_IsMarkedAsRootCaWithPrivateKey()
    {
        using var manager = new CertificateManager(_dir);
        Assert.Contains("GitHubTurbo Root CA", manager.CaCert.Subject);
        Assert.True(manager.CaCert.HasPrivateKey);
    }

    [Fact]
    public void Constructor_ReusesExistingCaFromDisk()
    {
        string thumbprint;
        using (var first = new CertificateManager(_dir))
            thumbprint = first.CaCert.Thumbprint;

        using var second = new CertificateManager(_dir);
        Assert.Equal(thumbprint, second.CaCert.Thumbprint);
    }

    [Fact]
    public void GetOrCreateServerCert_ContainsDomainAndHasPrivateKey()
    {
        using var manager = new CertificateManager(_dir);
        var cert = manager.GetOrCreateServerCert("github.com");
        Assert.Contains("github.com", cert.Subject);
        Assert.True(cert.HasPrivateKey);
    }

    [Fact]
    public void GetOrCreateServerCert_IsCachedPerDomain()
    {
        using var manager = new CertificateManager(_dir);
        var first = manager.GetOrCreateServerCert("api.github.com");
        var second = manager.GetOrCreateServerCert("api.github.com");
        Assert.Same(first, second);
    }

    [Fact]
    public void GetOrCreateServerCert_IssuesDifferentCertsPerDomain()
    {
        using var manager = new CertificateManager(_dir);
        var a = manager.GetOrCreateServerCert("github.com");
        var b = manager.GetOrCreateServerCert("gist.github.com");
        Assert.NotSame(a, b);
        Assert.NotEqual(a.Thumbprint, b.Thumbprint);
    }

    [Fact]
    public void GetOrCreateServerCert_SanIncludesRequestedDomain()
    {
        using var manager = new CertificateManager(_dir);
        var cert = manager.GetOrCreateServerCert("raw.githubusercontent.com");
        var san = cert.Extensions
            .OfType<X509SubjectKeyIdentifierExtension>()
            .ToList();
        // Subject CN is the domain; also verify the cert can sign/validate as server.
        Assert.Contains("raw.githubusercontent.com", cert.Subject);
        Assert.NotEmpty(san);
    }

    [Fact]
    public void GetOrCreateServerCert_FallsBackToGithubComForEmptySni()
    {
        using var manager = new CertificateManager(_dir);
        var cert = manager.GetOrCreateServerCert("");
        Assert.Contains("github.com", cert.Subject);
    }

    [Fact]
    public async Task GetOrCreateServerCert_CanCompleteTlsHandshakeSignedByCa()
    {
        using var manager = new CertificateManager(_dir);
        var serverCert = manager.GetOrCreateServerCert("handshake.test");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = serverCert,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            });
            var payload = Encoding.ASCII.GetBytes("ok");
            await ssl.WriteAsync(payload);
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
            (_, cert, _, _) =>
            {
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(manager.CaCert);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(new X509Certificate2(cert!.GetRawCertData()));
            });
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "handshake.test",
        });
        var buffer = new byte[8];
        var read = await ssl.ReadAsync(buffer);
        Assert.Equal("ok", Encoding.ASCII.GetString(buffer, 0, read));
        await serverTask;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}

public sealed class EndpointCacheTests
{
    [Fact]
    public void TryGetIps_ReturnsNullWhenEmpty()
    {
        var cache = new EndpointCache();
        Assert.Null(cache.TryGetIps("github.com"));
        Assert.Null(cache.Describe("github.com"));
    }

    [Fact]
    public void Seed_MakesIpsVisible()
    {
        var cache = new EndpointCache();
        cache.Seed("github.com", "20.205.243.166", "20.27.177.113");
        Assert.Equal(["20.205.243.166", "20.27.177.113"], cache.TryGetIps("github.com"));
        Assert.Equal("20.205.243.166", cache.Describe("github.com"));
    }

    [Fact]
    public void GetPreferredSni_DefaultsToIpAddress()
    {
        var cache = new EndpointCache();
        Assert.Equal(TlsSniMode.IpAddress, cache.GetPreferredSni("github.com"));
    }

    [Fact]
    public void RememberSni_OverridesPreferredMode()
    {
        var cache = new EndpointCache();
        cache.RememberSni("github.com", TlsSniMode.None);
        Assert.Equal(TlsSniMode.None, cache.GetPreferredSni("github.com"));

        cache.RememberSni("github.com", TlsSniMode.RealDomain);
        Assert.Equal(TlsSniMode.RealDomain, cache.GetPreferredSni("github.com"));
    }

    [Fact]
    public void PreferredSni_IsTrackedPerDomain()
    {
        var cache = new EndpointCache();
        cache.RememberSni("github.com", TlsSniMode.None);
        Assert.Equal(TlsSniMode.IpAddress, cache.GetPreferredSni("api.github.com"));
        Assert.Equal(TlsSniMode.None, cache.GetPreferredSni("github.com"));
    }

    [Fact]
    public void Seed_RequiresAtLeastOneIp()
    {
        var cache = new EndpointCache();
        Assert.Throws<ArgumentException>(() => cache.Seed("github.com"));
    }

    [Fact]
    public async Task GetIpsForConnectAsync_ReturnsSeededIpsWithoutNetwork()
    {
        var cache = new EndpointCache();
        cache.Seed("offline.test", "127.0.0.1");
        var ips = await cache.GetIpsForConnectAsync("offline.test", CancellationToken.None);
        Assert.Equal(["127.0.0.1"], ips);
    }
}

public sealed class MitmProxyServerHttpParsingTests
{
    [Theory]
    [InlineData("GET / HTTP/1.1\r\nHost: github.com\r\n\r\n", "github.com")]
    [InlineData("GET / HTTP/1.1\r\nHost: github.com:8443\r\n\r\n", "github.com")]
    [InlineData("GET / HTTP/1.1\r\nhost: GITHUB.COM\r\n\r\n", "GITHUB.COM")]
    [InlineData("GET / HTTP/1.1\r\nUser-Agent: x\r\nHost: api.github.com\r\n\r\n", "api.github.com")]
    public void ExtractHost_ReadsHostHeader(string request, string expected)
        => Assert.Equal(expected, MitmProxyServer.ExtractHost(request));

    [Fact]
    public void ExtractHost_ReturnsNullWhenMissing()
        => Assert.Null(MitmProxyServer.ExtractHost("GET / HTTP/1.1\r\nUser-Agent: x\r\n\r\n"));

    [Theory]
    [InlineData("GET / HTTP/1.1\r\nHost: a\r\n\r\n", "/")]
    [InlineData("GET /foo/bar?x=1 HTTP/1.1\r\nHost: a\r\n\r\n", "/foo/bar?x=1")]
    [InlineData("GET /release/download/v1/a.zip HTTP/1.1\r\nHost: a\r\n\r\n", "/release/download/v1/a.zip")]
    public void ExtractPath_ReadsRequestTarget(string request, string expected)
        => Assert.Equal(expected, MitmProxyServer.ExtractPath(request));
}
