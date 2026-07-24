# GitHub Turbo 反向代理重写计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 GitHub Turbo 从纯 hosts 文件方案重构为反向代理 + TLS 拦截方案，解决 SNI 阻断导致加速后仍无法访问 GitHub 的问题。

**Architecture:** 在本地运行一个基于 Kestrel 的代理服务器（监听 127.0.0.1:443），hosts 文件将 GitHub 域名指向 127.0.0.1。Kestrel 使用动态证书选择器为每个域名提供 CA 签名的证书。HTTP 中间件将请求转发到通过 DoH 解析的真实 IP，出站 TLS 连接使用 IP 地址作为 SNI 绕过阻断。

**Tech Stack:** .NET 8 WPF, Kestrel (ASP.NET Core), System.Security.Cryptography.X509Certificates

## Global Constraints

- 目标框架: net8.0-windows (WPF)
- 平台: Windows only (win-x64)
- 需要管理员权限（hosts 文件写入 + 证书安装 + 端口 443/80 监听）
- 保持单文件发布 (PublishSingleFile=true)
- 保持现有 UI 框架 (WPF + MVVM)
- 保持现有项目结构风格（file-scoped namespace, sealed class, record DTO）
- 不引入 YARP 等外部反向代理库，使用 Kestrel 原生中间件
- 代理服务器作为进程内宿主运行（In-process）

---

## 总体架构

```
浏览器 → github.com
  → DNS 返回 127.0.0.1（hosts 文件）
  → TCP 连接到 127.0.0.1:443
  → Kestrel 接收 TLS 连接
  → 证书选择器根据 SNI 返回对应域名证书（CA 签名）
  → 浏览器验证证书通过（CA 已信任）
  → HTTP 中间件处理请求：
    1. 从 Host 头提取域名
    2. 通过 DoH 解析真实 IP
    3. 建立到真实 IP 的 TCP 连接
    4. 外层 TLS 握手使用 IP 作为 SNI
    5. 转发 HTTP 请求/响应
```

## 文件结构

```
GitHubTurbo/
├── Proxy/
│   ├── ProxyServer.cs              # Kestrel 代理服务器宿主
│   ├── CertificateManager.cs       # CA 证书管理 + 域名证书缓存
│   ├── DomainResolver.cs           # DoH 域名解析
│   └── ProxyMiddleware.cs          # HTTP 请求转发中间件
├── AppController.cs                # 重构：代理启动/停止逻辑
├── HostsFileService.cs             # 修改：指向 127.0.0.1
├── NetworkDiagnostics.cs           # 修改：通过代理检测
├── AppPaths.cs                     # 修改：添加证书目录
├── MainWindow.xaml.cs              # 微调：状态显示
├── ViewModels.cs                   # 保持不变
├── GitHubPlatformProvider.cs       # 保持不变
├── GitLabPlatformProvider.cs       # 保持不变
└── GitHubTurbo.csproj              # 修改：移除 YARP，仅用 Kestrel
```

---

### Task 1: 添加项目依赖

**Files:**
- Modify: `GitHubTurbo.csproj`

**Interfaces:**
- Produces: 项目可引用 ASP.NET Core / Kestrel

- [ ] **Step 1: 添加 NuGet 包引用**

将 `GitHubTurbo.csproj` 的 `<PropertyGroup>` 后添加 `<ItemGroup>`：

```xml
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
```

完整文件内容：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>true</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PublishReadyToRun>true</PublishReadyToRun>
    <EnableCompressionInSingleFile>false</EnableCompressionInSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <DebugType>none</DebugType>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: 验证构建**

Run: `dotnet build -c Release`
Expected: 构建成功

- [ ] **Step 3: Commit**

```bash
git add GitHubTurbo.csproj
git commit -m "feat: add ASP.NET Core framework reference for Kestrel proxy"
```

---

### Task 2: 证书管理器

**Files:**
- Create: `Proxy/CertificateManager.cs`
- Modify: `AppPaths.cs`

**Interfaces:**
- Produces: `CertificateManager` class

- [ ] **Step 1: 添加证书目录到 AppPaths**

修改 `AppPaths.cs`，添加 `CertificateDirectory`：

```csharp
public static readonly string CertificateDirectory = Path.Combine(RootDirectory, "certs");
```

并在 `EnsureDirectories()` 中添加：

```csharp
Directory.CreateDirectory(CertificateDirectory);
```

- [ ] **Step 2: 实现证书管理器**

创建 `Proxy/CertificateManager.cs`：

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GitHubTurbo.Proxy;

public sealed class CertificateManager
{
    private readonly ConcurrentDictionary<string, X509Certificate2> _certCache = new();
    private readonly string _pfxPath;
    private readonly string _cerPath;
    private const string CaPassword = "GitHubTurbo";
    private const string CaSubject = "C=CN, O=GitHubTurbo, OU=Network Acceleration, CN=GitHubTurbo Root CA";

    public X509Certificate2 CaCert { get; }

    public CertificateManager()
    {
        Directory.CreateDirectory(AppPaths.CertificateDirectory);
        _pfxPath = Path.Combine(AppPaths.CertificateDirectory, "githubturbo-ca.pfx");
        _cerPath = Path.Combine(AppPaths.CertificateDirectory, "githubturbo-ca.cer");
        CaCert = LoadOrCreateCaCert();
    }

    private X509Certificate2 LoadOrCreateCaCert()
    {
        if (File.Exists(_pfxPath))
        {
            try
            {
                var cert = new X509Certificate2(_pfxPath, CaPassword, X509KeyStorageFlags.Exportable);
                if (cert.NotAfter > DateTime.Now.AddDays(30) && cert.HasPrivateKey)
                    return cert;
            }
            catch { }
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName(CaSubject),
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, true, 1, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.KeyCertSign, true));

        var oids = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1"),
            new("1.3.6.1.5.5.7.3.2")
        };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, true));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("GitHubTurbo Root CA");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var now = DateTimeOffset.UtcNow;
        var cert = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));

        var pfxBytes = cert.Export(X509ContentType.Pkcs12, CaPassword);
        File.WriteAllBytes(_pfxPath, pfxBytes);
        File.WriteAllBytes(_cerPath, cert.Export(X509ContentType.Cert));

        return new X509Certificate2(_pfxPath, CaPassword, X509KeyStorageFlags.Exportable);
    }

    public X509Certificate2 GetOrCreateServerCert(string domain)
    {
        return _certCache.GetOrAdd(domain, d => GenerateEndCert(d));
    }

    private X509Certificate2 GenerateEndCert(string domain)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={domain}"),
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));

        var oids = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1"),
            new("1.3.6.1.5.5.7.3.2")
        };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, true));

        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(CaCert.PublicKey, false));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(domain);
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(1);
        var serial = BitConverter.GetBytes(Random.Shared.NextInt64());

        using var cert = request.Create(CaCert, notBefore, notAfter, serial);
        return cert.CopyWithPrivateKey(rsa);
    }

    public void TrustRootCertificate()
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            var cert = new X509Certificate2(_cerPath);
            if (store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count == 0)
                store.Add(cert);
        }
        catch { }
    }

    public void RemoveRootCertificate()
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            var certs = store.Certificates.Find(X509FindType.FindBySubjectName, "GitHubTurbo Root CA", false);
            foreach (var item in certs)
                store.Remove(item);
        }
        catch { }
    }

    public bool IsTrusted()
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var certs = store.Certificates.Find(X509FindType.FindBySubjectName, "GitHubTurbo Root CA", false);
            return certs.Count > 0 && certs[0].NotAfter > DateTime.Now;
        }
        catch { return false; }
    }

    public string CerFilePath => _cerPath;
}
```

- [ ] **Step 3: 验证构建**

Run: `dotnet build -c Release`
Expected: 构建成功

- [ ] **Step 4: Commit**

```bash
git add Proxy/CertificateManager.cs AppPaths.cs
git commit -m "feat: add certificate manager with CA and per-domain cert generation"
```

---

### Task 3: 域名解析器

**Files:**
- Create: `Proxy/DomainResolver.cs`

**Interfaces:**
- Produces: `DomainResolver` class

- [ ] **Step 1: 实现域名解析器**

创建 `Proxy/DomainResolver.cs`：

```csharp
using System.Net;
using System.Net.Sockets;

namespace GitHubTurbo.Proxy;

public sealed class DomainResolver
{
    private static readonly string[] DohEndpoints =
    [
        "https://cloudflare-dns.com/dns-query?name={0}&type=A",
        "https://dns.google/resolve?name={0}&type=A",
        "https://doh.pub/dns-query?name={0}&type=A",
    ];

    public async Task<IPAddress?> ResolveAsync(string domain, CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var addresses = await Dns.GetHostAddressesAsync(domain, cts.Token);
            var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 is not null)
                return ipv4;
        }
        catch { }

        foreach (var endpoint in DohEndpoints)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                var ip = await ResolveViaDohAsync(domain, endpoint, cts.Token);
                if (ip is not null)
                    return ip;
            }
            catch { }
        }

        return null;
    }

    private static async Task<IPAddress?> ResolveViaDohAsync(string domain, string endpoint, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var uri = string.Format(endpoint, Uri.EscapeDataString(domain));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/dns-json");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("Answer", out var answers))
        {
            foreach (var answer in answers.EnumerateArray())
            {
                if (answer.TryGetProperty("type", out var type) && type.GetInt32() == 1 &&
                    answer.TryGetProperty("data", out var data))
                {
                    var ipStr = data.GetString();
                    if (!string.IsNullOrEmpty(ipStr) &&
                        IPAddress.TryParse(ipStr, out var addr) &&
                        addr.AddressFamily == AddressFamily.InterNetwork)
                        return addr;
                }
            }
        }
        return null;
    }
}
```

- [ ] **Step 2: 验证构建**

Run: `dotnet build -c Release`
Expected: 构建成功

- [ ] **Step 3: Commit**

```bash
git add Proxy/DomainResolver.cs
git commit -m "feat: add DoH domain resolver"
```

---

### Task 4: 代理中间件

**Files:**
- Create: `Proxy/ProxyMiddleware.cs`

**Interfaces:**
- Consumes: `DomainResolver`
- Produces: `ProxyMiddleware` class

- [ ] **Step 1: 实现代理中间件**

创建 `Proxy/ProxyMiddleware.cs`：

```csharp
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace GitHubTurbo.Proxy;

public sealed class ProxyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly DomainResolver _resolver;

    public ProxyMiddleware(RequestDelegate next, DomainResolver resolver)
    {
        _next = next;
        _resolver = resolver;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var host = context.Request.Host.Host;
        if (string.IsNullOrEmpty(host))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Missing Host header");
            return;
        }

        var ip = await _resolver.ResolveAsync(host, context.RequestAborted);
        if (ip == null)
        {
            context.Response.StatusCode = 502;
            await context.Response.WriteAsync($"无法解析域名 {host}，请检查网络连接。");
            return;
        }

        try
        {
            await ForwardRequestAsync(context, host, ip);
        }
        catch (OperationCanceledException)
        {
            if (!context.Response.HasStarted)
                context.Response.StatusCode = 504;
        }
        catch (Exception ex)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 502;
                await context.Response.WriteAsync($"代理错误: {ex.Message}");
            }
        }
    }

    private async Task ForwardRequestAsync(HttpContext context, string host, IPAddress ip)
    {
        var port = context.Request.IsHttps ? 443 : 80;
        using var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(new IPEndPoint(ip, port), cts.Token);
        var stream = new NetworkStream(socket, ownsSocket: true);

        if (port == 443)
        {
            var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = ip.ToString(),
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            }, cts.Token);
            await RelayHttpAsync(context, sslStream, host);
        }
        else
        {
            await RelayHttpAsync(context, stream, host);
        }
    }

    private async Task RelayHttpAsync(HttpContext context, Stream destStream, string host)
    {
        var request = context.Request;
        var headerBuilder = new StringBuilder();
        headerBuilder.Append($"{request.Method} {request.Path}{request.QueryString} HTTP/1.1\r\n");
        headerBuilder.Append($"Host: {host}\r\n");

        foreach (var header in request.Headers)
        {
            var key = header.Key;
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            headerBuilder.Append($"{key}: {header.Value}\r\n");
        }
        headerBuilder.Append("Connection: close\r\n");

        if (request.ContentLength > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
        {
            headerBuilder.Append($"Content-Length: {request.ContentLength ?? 0}\r\n");
        }
        headerBuilder.Append("\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(headerBuilder.ToString());
        await destStream.WriteAsync(headerBytes, context.RequestAborted);
        await destStream.FlushAsync(context.RequestAborted);

        if (request.ContentLength > 0)
        {
            await request.Body.CopyToAsync(destStream, context.RequestAborted);
            await destStream.FlushAsync(context.RequestAborted);
        }

        await RelayResponseAsync(context.Response, destStream);
    }

    private async Task RelayResponseAsync(HttpResponse response, Stream source)
    {
        var buffer = new byte[8192];
        var headerBuilder = new StringBuilder();
        var bodyStream = new MemoryStream();
        var headerComplete = false;
        var totalRead = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, 0, buffer.Length);
            if (read == 0) break;

            if (!headerComplete)
            {
                var text = Encoding.ASCII.GetString(buffer, 0, read);
                headerBuilder.Append(text);
                var headerEndIdx = headerBuilder.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);

                if (headerEndIdx >= 0)
                {
                    headerComplete = true;
                    var fullHeader = headerBuilder.ToString();
                    var headerPart = fullHeader[..(headerEndIdx + 4)];
                    var bodyStart = headerEndIdx + 4;
                    var bodyLen = read - (bodyStart - (fullHeader.Length - text.Length));

                    ParseResponseHeaders(response, headerPart);

                    if (bodyLen > 0)
                    {
                        var bodyBytes = new byte[bodyLen];
                        Array.Copy(buffer, buffer.Length - bodyLen, bodyBytes, 0, bodyLen);
                        await bodyStream.WriteAsync(bodyBytes);
                    }
                }
            }
            else
            {
                await bodyStream.WriteAsync(buffer.AsMemory(0, read));
            }
        }

        if (!headerComplete)
        {
            response.StatusCode = 502;
            return;
        }

        response.ContentLength = bodyStream.Length;
        bodyStream.Position = 0;
        await bodyStream.CopyToAsync(response.Body);
    }

    private void ParseResponseHeaders(HttpResponse response, string headerText)
    {
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return;

        var statusParts = lines[0].Split(' ', 3);
        if (statusParts.Length >= 2 && int.TryParse(statusParts[1], out var statusCode))
            response.StatusCode = statusCode;

        for (var i = 1; i < lines.Length; i++)
        {
            var colonIdx = lines[i].IndexOf(':');
            if (colonIdx < 0) continue;

            var key = lines[i][..colonIdx].Trim();
            var value = lines[i][(colonIdx + 1)..].Trim();

            if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;
            if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                continue;
            if (key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                continue;

            response.Headers[key] = value;
        }
    }
}
```

- [ ] **Step 2: 验证构建**

Run: `dotnet build -c Release`
Expected: 构建成功

- [ ] **Step 3: Commit**

```bash
git add Proxy/ProxyMiddleware.cs
git commit -m "feat: add HTTP proxy middleware with SNI bypass"
```

---

### Task 5: 代理服务器宿主

**Files:**
- Create: `Proxy/ProxyServer.cs`

**Interfaces:**
- Consumes: `CertificateManager`, `DomainResolver`, `ProxyMiddleware`
- Produces: `ProxyServer` class

- [ ] **Step 1: 实现代理服务器**

创建 `Proxy/ProxyServer.cs`：

```csharp
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace GitHubTurbo.Proxy;

public sealed class ProxyServer : IDisposable
{
    private WebApplication? _app;
    private readonly CertificateManager _certManager;
    private readonly DomainResolver _resolver;
    private bool _isRunning;

    public bool IsRunning => _isRunning;

    public ProxyServer(CertificateManager certManager, DomainResolver resolver)
    {
        _certManager = certManager;
        _resolver = resolver;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning) return;

        _certManager.TrustRootCertificate();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 443, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                listenOptions.UseHttps(httpsOptions =>
                {
                    httpsOptions.ServerCertificateSelector = (context, sni) =>
                    {
                        if (string.IsNullOrEmpty(sni))
                            return _certManager.CaCert;
                        return _certManager.GetOrCreateServerCert(sni);
                    };
                });
            });
            options.Listen(IPAddress.Loopback, 80, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
            });
        });

        builder.Services.AddSingleton(_resolver);
        _app = builder.Build();
        _app.Use<ProxyMiddleware>();
        _app.MapFallback(async context =>
        {
            context.Response.StatusCode = 200;
            await context.Response.WriteAsync("GitHub Turbo Proxy Running");
        });

        await _app.StartAsync(cancellationToken);
        _isRunning = true;
    }

    public async Task StopAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
        _isRunning = false;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}
```

- [ ] **Step 2: 验证构建**

Run: `dotnet build -c Release`
Expected: 构建成功

- [ ] **Step 3: Commit**

```bash
git add Proxy/ProxyServer.cs
git commit -m "feat: add Kestrel-based proxy server host with dynamic cert selection"
```

---

### Task 6: 重构 AppController

**Files:**
- Modify: `AppController.cs`

**Interfaces:**
- Consumes: `ProxyServer`, `HostsFileService`
- Produces: Updated `AppController`

- [ ] **Step 1: 重构 AppController**

重写 `AppController.cs`：

```csharp
namespace GitHubTurbo;

public sealed class AppController : IDisposable
{
    readonly SemaphoreSlim operationLock = new(1, 1);
    readonly Dictionary<string, TurboStatus> platformStatuses = new();
    readonly CertificateManager certManager;
    readonly DomainResolver resolver;
    ProxyServer? proxyServer;

    public IReadOnlyDictionary<string, TurboStatus> PlatformStatuses => platformStatuses;
    public event EventHandler<PlatformStatusChangedEventArgs>? PlatformStatusChanged;
    public bool AnyRunning => platformStatuses.Values.Any(s => s.IsRunning);

    public AppController()
    {
        certManager = new();
        resolver = new();
    }

    public async Task InitializeAsync()
    {
        AppPaths.EnsureDirectories();
        await AppLogger.InfoAsync("GitHub Turbo started.");

        foreach (var provider in PlatformProviders.All)
        {
            if (HostsFileService.HasEntries(provider.Descriptor.Id) || HostsFileService.HasLegacyEntries)
            {
                platformStatuses[provider.Descriptor.Id] = new TurboStatus(true, $"{provider.Descriptor.Title} 加速已开启",
                    "代理加速运行中。", "已修改", "未检测", "可执行网络检测以验证连通性。");
            }
            else
            {
                platformStatuses[provider.Descriptor.Id] = TurboStatus.Stopped;
            }
        }
    }

    public async Task StartPlatformAsync(string platformId)
    {
        var provider = PlatformProviders.GetById(platformId);
        if (provider is null)
        {
            await AppLogger.ErrorAsync($"Unknown platform: {platformId}");
            return;
        }

        await operationLock.WaitAsync();
        try
        {
            if (HostsFileService.HasEntries(platformId))
            {
                var status = new TurboStatus(true, $"{provider.Descriptor.Title} 加速已开启",
                    "代理加速运行中。", "已修改", "未检测", "可执行网络检测以验证连通性。");
                UpdatePlatform(platformId, status);
                await RunDiagnosticsCoreAsync(platformId);
                return;
            }

            if (proxyServer == null)
            {
                proxyServer = new ProxyServer(certManager, resolver);
                await proxyServer.StartAsync();
            }

            var domains = provider.Domains.Select(d => d.Domain).Distinct().ToArray();
            var hostEntries = domains.ToDictionary(
                d => d,
                d => "127.0.0.1",
                StringComparer.OrdinalIgnoreCase);

            var ok = HostsFileService.ApplyHosts(platformId, hostEntries);
            if (!ok)
            {
                UpdatePlatform(platformId, TurboStatus.Stopped with { Detail = "写入 hosts 文件失败，请以管理员身份运行程序。" });
                return;
            }

            var newStatus = new TurboStatus(true, $"{provider.Descriptor.Title} 加速已开启",
                $"已写入 {hostEntries.Count} 条 hosts 记录，代理服务器已启动。", "已修改", "未检测", "可执行网络检测以验证连通性。");
            UpdatePlatform(platformId, newStatus);
            await AppLogger.InfoAsync($"{provider.Descriptor.Title} proxy acceleration enabled.");
            await RunDiagnosticsCoreAsync(platformId);
        }
        catch (Exception ex)
        {
            await AppLogger.ErrorAsync($"Start failed for {platformId}: {ex.GetType().Name}.");
            UpdatePlatform(platformId, TurboStatus.Stopped with { Detail = ex.Message });
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task StopPlatformAsync(string platformId)
    {
        var provider = PlatformProviders.GetById(platformId);
        if (provider is null)
        {
            await AppLogger.ErrorAsync($"Unknown platform: {platformId}");
            return;
        }

        await operationLock.WaitAsync();
        try
        {
            if (!HostsFileService.HasEntries(platformId))
            {
                UpdatePlatform(platformId, TurboStatus.Stopped);
                return;
            }

            var ok = HostsFileService.RemoveHosts(platformId);
            if (!ok)
            {
                UpdatePlatform(platformId, platformStatuses.GetValueOrDefault(platformId) ?? TurboStatus.Stopped with { Detail = "还原 hosts 文件失败，请以管理员身份运行程序。" });
                return;
            }

            if (!AnyRunning && proxyServer != null)
            {
                await proxyServer.StopAsync();
                proxyServer.Dispose();
                proxyServer = null;
            }

            UpdatePlatform(platformId, TurboStatus.Stopped);
            await AppLogger.InfoAsync($"{provider.Descriptor.Title} proxy acceleration disabled.");
        }
        catch (Exception ex)
        {
            await AppLogger.ErrorAsync($"Stop failed for {platformId}: {ex.GetType().Name}.");
            UpdatePlatform(platformId, platformStatuses.GetValueOrDefault(platformId) ?? TurboStatus.Stopped with { Detail = ex.Message });
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task StartAllAsync()
    {
        foreach (var provider in PlatformProviders.All)
            await StartPlatformAsync(provider.Descriptor.Id);
    }

    public async Task StopAllAsync()
    {
        foreach (var provider in PlatformProviders.All)
            await StopPlatformAsync(provider.Descriptor.Id);
    }

    public async Task RunDiagnosticsAsync(string platformId)
    {
        await operationLock.WaitAsync();
        try
        {
            if (!HostsFileService.HasEntries(platformId))
            {
                UpdatePlatform(platformId, TurboStatus.Stopped with { Detail = "请先启动加速，再执行网络检测。" });
                return;
            }
            await RunDiagnosticsCoreAsync(platformId);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<IReadOnlyList<DomainDiagnosticResult>> RunDomainDiagnosticsAsync(string platformId)
    {
        await operationLock.WaitAsync();
        try
        {
            if (!HostsFileService.HasEntries(platformId))
                return [];

            var provider = PlatformProviders.GetById(platformId);
            if (provider is null)
                return [];

            var hosts = provider.Domains.Select(d => d.Domain).ToArray();
            var tasks = hosts.Select(async host =>
            {
                var result = await NetworkDiagnostics.CheckHostAsync(host, CancellationToken.None);
                return new DomainDiagnosticResult(host, result.Success, result.Latency, result.Detail);
            });

            var results = (await Task.WhenAll(tasks)).ToList();
            var overall = results.FirstOrDefault(x => x.Success) ?? new DomainDiagnosticResult(provider.Domains[0].Domain, false, "不可用", "所有域名检测失败。");
            UpdatePlatform(platformId, platformStatuses.GetValueOrDefault(platformId) ?? TurboStatus.Stopped with
            {
                Detail = $"网络测试已完成，{results.Count} 个域名可查看结果。",
                Latency = overall.Latency,
                LastChecked = $"最后检测时间：{DateTime.Now:HH:mm:ss}",
            });

            return results;
        }
        finally
        {
            operationLock.Release();
        }
    }

    async Task RunDiagnosticsCoreAsync(string platformId)
    {
        var provider = PlatformProviders.GetById(platformId);
        if (provider is null || provider.Domains.Count == 0)
            return;

        var primaryDomain = provider.Domains[0].Domain;
        var result = await NetworkDiagnostics.CheckHostAsync(primaryDomain, CancellationToken.None);
        var checkedAt = $"最后检测时间：{DateTime.Now:HH:mm:ss}";
        UpdatePlatform(platformId, platformStatuses.GetValueOrDefault(platformId) ?? TurboStatus.Stopped with
        {
            Detail = result.Detail,
            Latency = result.Latency,
            LastChecked = checkedAt,
        });
        await AppLogger.InfoAsync($"{provider.Descriptor.Title} network test: {(result.Success ? "success" : "failed")}.");
    }

    void UpdatePlatform(string platformId, TurboStatus status)
    {
        platformStatuses[platformId] = status;
        PlatformStatusChanged?.Invoke(this, new PlatformStatusChangedEventArgs(platformId, status));
    }

    public void Dispose()
    {
        proxyServer?.Dispose();
        operationLock.Dispose();
    }
}

public sealed record TurboStatus(bool IsRunning, string Title, string Detail, string SystemProxy, string Latency, string LastChecked)
{
    public static readonly TurboStatus Stopped = new(false, "未启动", "点击启动后，将通过代理服务器加速访问。", "未修改", "未检测", "尚未执行网络检测。");
}

public sealed record DomainDiagnosticResult(string Host, bool Success, string Latency, string Detail);
public sealed record PlatformStatusChangedEventArgs(string PlatformId, TurboStatus Status);
```

- [ ] **Step 2: 验证构建**

Run: `dotnet build -c Release`
Expected: 构建成功

- [ ] **Step 3: Commit**

```bash
git add AppController.cs
git commit -m "feat: refactor AppController to use proxy server"
```

---

### Task 7: 更新网络检测

**Files:**
- Modify: `NetworkDiagnostics.cs`

**Interfaces:**
- Produces: Updated `CheckHostAsync`

- [ ] **Step 1: 更新网络检测**

修改 `NetworkDiagnostics.cs`：

```csharp
using System.Diagnostics;
using System.Net.Sockets;

namespace GitHubTurbo;

public static class NetworkDiagnostics
{
    public static async Task<DiagnosticResult> CheckHostAsync(string host, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient { NoDelay = true };
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync("127.0.0.1", 443, timeoutCts.Token);
            watch.Stop();

            using var stream = client.GetStream();
            stream.ReadTimeout = 3000;
            stream.WriteTimeout = 3000;

            var requestBytes = System.Text.Encoding.ASCII.GetBytes(
                $"HEAD / HTTP/1.1\r\nHost: {host}\r\nUser-Agent: GitHubTurbo/1.0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(requestBytes, timeoutCts.Token);
            await stream.FlushAsync(timeoutCts.Token);

            var buffer = new byte[4096];
            var read = await stream.ReadAsync(buffer, timeoutCts.Token);
            watch.Stop();

            if (read > 0)
            {
                var response = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
                var statusLine = response.Split('\r')[0];
                return new DiagnosticResult(true, $"{watch.ElapsedMilliseconds} ms", $"{host} {statusLine}");
            }

            return new DiagnosticResult(true, $"{watch.ElapsedMilliseconds} ms", $"{host} TCP 连接成功。");
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return new DiagnosticResult(false, "超时", $"{host} 连接超时。");
        }
        catch (Exception ex)
        {
            watch.Stop();
            return new DiagnosticResult(false, "不可用", $"{host} 连接失败：{ex.GetType().Name}。");
        }
    }
}

public sealed record DiagnosticResult(bool Success, string Latency, string Detail);
```

- [ ] **Step 2: 验证构建**

Run: `dotnet build -c Release`
Expected: 构建成功

- [ ] **Step 3: Commit**

```bash
git add NetworkDiagnostics.cs
git commit -m "feat: update network diagnostics to test through local proxy"
```

---

### Task 8: 集成验证

**Files:**
- 无新文件

- [ ] **Step 1: 完整构建**

Run: `dotnet build -c Release`
Expected: 0 错误，0 警告

- [ ] **Step 2: 启动验证**

Run: `dotnet run -c Release`（管理员权限）
Expected: 应用启动正常，UI 显示正常

- [ ] **Step 3: 功能验证清单**

1. 点击"启动加速" → hosts 文件写入 `127.0.0.1 github.com` 等条目
2. 确认 Kestrel 在 127.0.0.1:443 启动
3. 确认 CA 证书生成到 `%LOCALAPPDATA%\GitHubTurbo\certs\`
4. 确认 CA 证书安装到系统信任根存储
5. 浏览器访问 `https://github.com` 应正常加载（无证书警告）
6. 点击"停止" → hosts 条目移除，代理停止

- [ ] **Step 4: Commit**

```bash
git commit --allow-empty -m "test: verify proxy acceleration end-to-end"
```

---

## 技术注意事项

### Kestrel 端口绑定
- 端口 443 和 80 需要管理员权限
- 如果端口被占用（如 IIS、其他代理软件），需要检测并提示用户
- 可考虑配置备用端口（如 8443/8080）

### 证书信任
- 首次启动时自动安装 CA 证书
- 停止时保留证书（避免频繁安装/卸载影响其他应用）
- 提供"卸载证书"选项

### 性能优化
- 证书缓存（已生成域名证书不重复生成）
- TCP 连接复用
- 异步全链路处理

### 与 SteamTools 的差异
- 仅支持 hosts 模式（不支持系统代理/PAC/DNS 拦截）
- 无脚本注入
- 无服务端代理
- 无 Git/SSH TCP 代理（后续可扩展）
- 使用 Kestrel 原生中间件而非 YARP（更轻量）
