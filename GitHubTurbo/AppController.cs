namespace GitHubTurbo;

public sealed class AppController : IDisposable
{
    readonly SemaphoreSlim operationLock = new(1, 1);
    readonly Dictionary<string, TurboStatus> platformStatuses = new();

    public IReadOnlyDictionary<string, TurboStatus> PlatformStatuses => platformStatuses;

    public event EventHandler<PlatformStatusChangedEventArgs>? PlatformStatusChanged;

    public bool AnyRunning => platformStatuses.Values.Any(s => s.IsRunning);

    public async Task InitializeAsync()
    {
        AppPaths.EnsureDirectories();
        await AppLogger.InfoAsync("GitHub Turbo started.");

        foreach (var provider in PlatformProviders.All)
        {
            if (HostsFileService.HasEntries(provider.Descriptor.Id) || HostsFileService.HasLegacyEntries)
            {
                platformStatuses[provider.Descriptor.Id] = new TurboStatus(true, $"{provider.Descriptor.Title} 加速已开启",
                    "hosts 文件已写入加速条目。", "已修改", "未检测", "可执行网络检测以验证连通性。");
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
                    "hosts 文件已写入加速条目。", "已修改", "未检测", "可执行网络检测以验证连通性。");
                UpdatePlatform(platformId, status);
                await RunDiagnosticsCoreAsync(platformId);
                return;
            }

            var ips = await ResolvePlatformIpsAsync(provider);
            if (ips.Count == 0)
            {
                UpdatePlatform(platformId, TurboStatus.Stopped with { Detail = "无法解析域名 IP，请检查网络连接。" });
                return;
            }

            var ok = HostsFileService.ApplyHosts(platformId, ips);
            if (!ok)
            {
                UpdatePlatform(platformId, TurboStatus.Stopped with { Detail = "写入 hosts 文件失败，请以管理员身份运行程序。" });
                return;
            }

            var newStatus = new TurboStatus(true, $"{provider.Descriptor.Title} 加速已开启",
                $"已写入 {ips.Count} 条 hosts 记录。", "已修改", "未检测", "可执行网络检测以验证连通性。");
            UpdatePlatform(platformId, newStatus);
            await AppLogger.InfoAsync($"{provider.Descriptor.Title} hosts acceleration enabled.");
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

            UpdatePlatform(platformId, TurboStatus.Stopped);
            await AppLogger.InfoAsync($"{provider.Descriptor.Title} hosts acceleration disabled.");
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
        {
            await StartPlatformAsync(provider.Descriptor.Id);
        }
    }

    public async Task StopAllAsync()
    {
        foreach (var provider in PlatformProviders.All)
        {
            await StopPlatformAsync(provider.Descriptor.Id);
        }
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

    async Task<Dictionary<string, string>> ResolvePlatformIpsAsync(IPlatformProvider provider)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var domains = provider.Domains.Select(d => d.Domain).Distinct().ToArray();

        var tasks = domains.Select(async domain =>
        {
            var ip = await ResolveIpAsync(domain);
            if (ip is not null)
            {
                lock (dict)
                {
                    dict[domain] = ip;
                }
            }
        });

        await Task.WhenAll(tasks);
        return dict;
    }

    static async Task<string?> ResolveIpAsync(string domain)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var addresses = await System.Net.Dns.GetHostAddressesAsync(domain, cts.Token);
            var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (ipv4 is not null)
                return ipv4.ToString();
        }
        catch { }

        foreach (var endpoint in DohEndpoints)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var ip = await ResolveViaDohAsync(domain, endpoint, cts.Token);
                if (ip is not null)
                    return ip;
            }
            catch { }
        }

        await AppLogger.ErrorAsync($"DNS resolve failed for {domain}: all methods failed");
        return null;
    }

    static readonly string[] DohEndpoints =
    [
        "https://cloudflare-dns.com/dns-query?name={0}&type=A",
        "https://dns.google/resolve?name={0}&type=A",
        "https://doh.pub/dns-query?name={0}&type=A",
    ];

    static async Task<string?> ResolveViaDohAsync(string domain, string endpoint, CancellationToken cancellationToken)
    {
        using var client = new System.Net.Http.HttpClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        var uri = string.Format(endpoint, Uri.EscapeDataString(domain));
        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, uri);
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
                    var ip = data.GetString();
                    if (!string.IsNullOrEmpty(ip) && System.Net.IPAddress.TryParse(ip, out var addr) &&
                        addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return ip;
                }
            }
        }
        return null;
    }

    void UpdatePlatform(string platformId, TurboStatus status)
    {
        platformStatuses[platformId] = status;
        PlatformStatusChanged?.Invoke(this, new PlatformStatusChangedEventArgs(platformId, status));
    }

    public void Dispose()
    {
        operationLock.Dispose();
    }
}

public sealed record TurboStatus(bool IsRunning, string Title, string Detail, string SystemProxy, string Latency, string LastChecked)
{
    public static readonly TurboStatus Stopped = new(false, "未启动", "点击启动后，将修改 hosts 文件加速访问。", "未修改", "未检测", "尚未执行网络检测。");
}

public sealed record DomainDiagnosticResult(string Host, bool Success, string Latency, string Detail);

public sealed record PlatformStatusChangedEventArgs(string PlatformId, TurboStatus Status);
