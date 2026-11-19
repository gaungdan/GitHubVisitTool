using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using GitHubTurbo.Proxy;

namespace GitHubTurbo;

public sealed class AppController : IDisposable
{
    internal static readonly SemaphoreSlim HostsFileLock = new(1, 1);
    internal static readonly SemaphoreSlim IpProbeGate = new(16, 16);
    internal static readonly SemaphoreSlim DnsResolutionGate = new(16, 16);
    static readonly HttpClient DohClient = new() { Timeout = TimeSpan.FromSeconds(4) };
    static Task<IReadOnlyList<CommunityIpEntry>>? _communityCacheTask;
    static readonly object _communityCacheLock = new();
    readonly Dictionary<string, TurboStatus> platformStatuses = new();
    readonly CertificateManager _certManager = new();
    readonly EndpointCache _endpointCache = new();
    readonly MitmProxyServer _proxyServer;
    readonly SemaphoreSlim _proxyLock = new(1, 1);
    IpRefreshService _refreshService = null!;
    AppSettings settings = new();

    public IReadOnlyDictionary<string, TurboStatus> PlatformStatuses => platformStatuses;

    public AppSettings Settings => settings;

    public event EventHandler<PlatformStatusChangedEventArgs>? PlatformStatusChanged;

    public bool AnyRunning => platformStatuses.Values.Any(s => s.IsRunning);

    public AppController()
    {
        _proxyServer = new MitmProxyServer(_certManager, _endpointCache);
    }

    public string? GetBackendIp(string domain) => _endpointCache.Describe(domain);

    public async Task InitializeAsync()
    {
        AppPaths.EnsureDirectories();
        settings = await AppSettings.LoadAsync();
        _refreshService = new IpRefreshService(_endpointCache);
        await AppLogger.InfoAsync("GitHub Turbo started.");

        var needProxy = false;
        foreach (var provider in PlatformProviders.All)
        {
            var conflicts = HostsFileService.FindConflictingEntries(provider);
            var hasEntries = HostsFileService.HasEntries(provider.Descriptor.Id);
            var hasLegacyEntries = provider.HasLegacyEntries;
            if (conflicts.Count > 0)
            {
                platformStatuses[provider.Descriptor.Id] = BuildConflictStatus(provider, conflicts);
                continue;
            }

            if (!hasEntries && !hasLegacyEntries)
            {
                platformStatuses[provider.Descriptor.Id] = TurboStatus.Stopped;
                continue;
            }

            var entries = HostsFileService.ReadEntries(provider.Descriptor.Id);
            var hasLoopback = entries.Values.Any(IpAddressHelpers.IsPrivateOrLoopback);
            var hasLegacyIps = hasLegacyEntries || (entries.Count > 0 && !hasLoopback);

            if (hasLegacyIps)
            {
                platformStatuses[provider.Descriptor.Id] = new TurboStatus(false, "需刷新",
                    "检测到旧版 hosts 条目。点击启动将切换为本地代理加速。", "需刷新", "未检测", "可执行网络检测以验证连通性。");
                continue;
            }

            // Loopback entries mean MITM mode is armed; the proxy must be up or browsing breaks.
            needProxy = true;
            platformStatuses[provider.Descriptor.Id] = BuildRunningStatus(provider, "本地代理加速运行中，hosts 已指向 127.0.0.1。");
        }

        if (needProxy)
            await EnsureProxyRunningAsync();

        if (settings.AutoRefreshOnLaunch)
        {
            foreach (var platformId in settings.EnabledPlatforms.ToArray())
                await StartPlatformAsync(platformId);
        }
    }

    public async Task StartPlatformAsync(string platformId, IProgress<PlatformProgressEventArgs>? progress = null)
    {
        var provider = PlatformProviders.GetById(platformId);
        if (provider is null)
        {
            await AppLogger.ErrorAsync($"Unknown platform: {platformId}");
            return;
        }

        try
        {
            await AppLogger.InfoAsync($"Start acceleration for {provider.Descriptor.Title}.");
            progress?.Report(new(platformId, $"正在检查 {provider.Descriptor.Title} Hosts 冲突…", 0, 100));

            var conflicts = HostsFileService.FindConflictingEntries(provider);
            if (conflicts.Count > 0)
            {
                UpdatePlatform(platformId, BuildConflictStatus(provider, conflicts));
                await AppLogger.ErrorAsync($"Conflicting hosts entries detected for {provider.Descriptor.Title}: {string.Join(", ", conflicts.Select(x => $"{x.Domain}={x.Ip}"))}");
                return;
            }

            progress?.Report(new(platformId, "正在启动本地代理…", 5, 100));
            var proxyError = await EnsureProxyRunningAsync();
            if (proxyError is not null)
            {
                UpdatePlatform(platformId, TurboStatus.Stopped with { Detail = proxyError });
                return;
            }

            progress?.Report(new(platformId, $"正在写入 {provider.Descriptor.Title} Hosts 记录…", 15, 100));
            var domains = provider.Domains.Select(d => d.Domain).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var loopbackEntries = domains.ToDictionary(d => d, _ => "127.0.0.1", StringComparer.OrdinalIgnoreCase);

            await HostsFileLock.WaitAsync();
            try
            {
                var applied = HostsFileService.TryApplyHosts(platformId, loopbackEntries);
                await AppLogger.InfoAsync($"Apply loopback hosts for {provider.Descriptor.Title}: {applied.Message}");
                if (!applied.Success)
                {
                    UpdatePlatform(platformId, TurboStatus.Stopped with { Detail = applied.Message });
                    return;
                }

                settings.SetPlatformEnabled(platformId, true);
                await settings.SaveAsync();
            }
            finally
            {
                HostsFileLock.Release();
            }

            UpdatePlatform(platformId, BuildRunningStatus(provider,
                $"已指向本地代理（127.0.0.1），共 {domains.Length} 个域名。"));
            await AppLogger.InfoAsync($"{provider.Descriptor.Title} MITM acceleration enabled.");

            // Warm the endpoint cache so the first browser hit is fast and the UI can show real IPs.
            progress?.Report(new(platformId, $"正在为 {provider.Descriptor.Title} 优选出口 IP…", 25, 100));
            await WarmupEndpointsAsync(platformId, domains, progress);

            progress?.Report(new(platformId, $"正在检测 {provider.Descriptor.Title} 网络连通性…", 92, 100));
            await RunDiagnosticsCoreAsync(platformId, progress);
            progress?.Report(new(platformId, $"{provider.Descriptor.Title} 加速完成", 100, 100));
        }
        catch (Exception ex)
        {
            await AppLogger.ErrorAsync($"Start failed for {platformId}: {ex.GetType().Name}: {ex.Message}");
            UpdatePlatform(platformId, TurboStatus.Stopped with { Detail = ex.Message });
        }
    }

    async Task WarmupEndpointsAsync(string platformId, IReadOnlyList<string> domains, IProgress<PlatformProgressEventArgs>? progress)
    {
        var completed = 0;
        var tasks = domains.Select(async domain =>
        {
            await IpProbeGate.WaitAsync();
            try
            {
                await _endpointCache.WarmupAsync(domain);
            }
            finally
            {
                IpProbeGate.Release();
            }
            var counted = Interlocked.Increment(ref completed);
            progress?.Report(new(platformId, $"正在探测 {domain}… ({counted}/{domains.Count})",
                25 + (int)((double)counted / domains.Count * 65), 100));
        });
        await Task.WhenAll(tasks);
    }

    async Task<string?> EnsureProxyRunningAsync()
    {
        await _proxyLock.WaitAsync();
        try
        {
            if (_proxyServer.IsRunning)
                return null;

            _proxyServer.Start();
            return null;
        }
        catch (Exception ex)
        {
            await AppLogger.ErrorAsync($"Start MITM proxy failed: {ex.GetType().Name}: {ex.Message}");
            return $"本地代理启动失败：{ex.Message}";
        }
        finally
        {
            _proxyLock.Release();
        }
    }

    async Task StopProxyIfIdleAsync()
    {
        if (AnyRunning)
            return;

        await _proxyLock.WaitAsync();
        try
        {
            if (_proxyServer.IsRunning)
                await _proxyServer.StopAsync();
        }
        finally
        {
            _proxyLock.Release();
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

        try
        {
            await HostsFileLock.WaitAsync();
            try
            {
                if (!HostsFileService.HasEntries(platformId))
                {
                    settings.SetPlatformEnabled(platformId, false);
                    await settings.SaveAsync();
                    UpdatePlatform(platformId, TurboStatus.Stopped);
                    await StopProxyIfIdleAsync();
                    return;
                }

                var removed = HostsFileService.TryRemoveHosts(platformId);
                await AppLogger.InfoAsync($"Remove hosts for {provider.Descriptor.Title}: {removed.Message}");
                if (!removed.Success)
                {
                    UpdatePlatform(platformId, platformStatuses.GetValueOrDefault(platformId) ?? TurboStatus.Stopped with { Detail = removed.Message });
                    return;
                }

                settings.SetPlatformEnabled(platformId, false);
                await settings.SaveAsync();
                UpdatePlatform(platformId, TurboStatus.Stopped);
                await AppLogger.InfoAsync($"{provider.Descriptor.Title} MITM acceleration disabled.");
            }
            finally
            {
                HostsFileLock.Release();
            }

            await StopProxyIfIdleAsync();
        }
        catch (Exception ex)
        {
            await AppLogger.ErrorAsync($"Stop failed for {platformId}: {ex.GetType().Name}: {ex.Message}");
            UpdatePlatform(platformId, platformStatuses.GetValueOrDefault(platformId) ?? TurboStatus.Stopped with { Detail = ex.Message });
        }
    }

    public async Task StartAllAsync(IProgress<PlatformProgressEventArgs>? progress = null)
    {
        // Sequential: the proxy and hosts file are shared resources.
        foreach (var provider in PlatformProviders.All)
            await StartPlatformAsync(provider.Descriptor.Id, progress);
    }

    public async Task StopAllAsync()
    {
        foreach (var provider in PlatformProviders.All)
            await StopPlatformAsync(provider.Descriptor.Id);
    }

    public async Task SetAutoRefreshOnLaunchAsync(bool value)
    {
        settings.AutoRefreshOnLaunch = value;
        await settings.SaveAsync();
    }

    public async Task RunDiagnosticsAsync(string platformId, IProgress<PlatformProgressEventArgs>? progress = null)
    {
        if (!HostsFileService.HasEntries(platformId))
        {
            UpdatePlatform(platformId, TurboStatus.Stopped with { Detail = "请先启动加速，再执行网络检测。" });
            return;
        }
        await RunDiagnosticsCoreAsync(platformId, progress);
    }

    public async Task<IReadOnlyList<DomainDiagnosticResult>> RunDomainDiagnosticsAsync(
        string platformId, IProgress<PlatformProgressEventArgs>? progress = null)
    {
        if (!HostsFileService.HasEntries(platformId))
            return [];

        var provider = PlatformProviders.GetById(platformId);
        if (provider is null)
            return [];

        var hosts = PlatformDiagnostics.GetDiagnosticDomains(provider).Select(d => d.Domain).ToArray();
        var completedCount = 0;

        var tasks = hosts.Select(async host =>
        {
            // Through-proxy check exercises the real browsing path (MITM + outbound).
            var result = await NetworkDiagnostics.CheckHostAsync(host, CancellationToken.None);
            var backendIp = _endpointCache.Describe(host);
            var detail = backendIp is null ? result.Detail : $"{backendIp} / {result.Detail}";
            var counted = Interlocked.Increment(ref completedCount);
            progress?.Report(new(platformId, $"正在检测 {host}… ({counted}/{hosts.Length})",
                (int)((double)completedCount / hosts.Length * 100), 100));
            return new DomainDiagnosticResult(host, backendIp ?? "-", result.Success, result.Latency, detail);
        });

        var results = (await Task.WhenAll(tasks)).ToList();

        var repaired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failedDomains = results.Where(r => !r.Success).Select(r => r.Host).ToList();
        if (failedDomains.Count > 0 && failedDomains.Count < results.Count)
        {
            foreach (var domain in failedDomains.Take(8))
                await _endpointCache.WarmupAsync(domain);
            for (var index = 0; index < results.Count; index++)
            {
                var host = results[index].Host;
                if (!failedDomains.Contains(host))
                    continue;
                var recheck = await NetworkDiagnostics.CheckHostAsync(host, CancellationToken.None);
                results[index] = new DomainDiagnosticResult(host, _endpointCache.Describe(host) ?? "-",
                    recheck.Success, recheck.Latency,
                    recheck.Success ? $"已刷新出口 IP：{recheck.Detail}" : recheck.Detail);
                if (recheck.Success)
                    repaired[host] = _endpointCache.Describe(host) ?? "-";
            }
        }

        var successCount = results.Count(x => x.Success);
        var overall = results.FirstOrDefault(x => x.Success) ??
            new DomainDiagnosticResult(provider.Domains[0].Domain, "-", false, "不可用", "所有域名检测失败。");

        var repairNote = repaired.Count > 0 ? $"已自动更换 {repaired.Count} 个出口 IP。" : string.Empty;
        UpdatePlatform(platformId, platformStatuses.GetValueOrDefault(platformId) ?? TurboStatus.Stopped with
        {
            Detail = $"网络测试已完成，{successCount}/{results.Count} 个域名可用。{repairNote}",
            Latency = overall.Latency,
            LastChecked = $"最后检测时间：{DateTime.Now:HH:mm:ss}",
        });

        return results;
    }

    async Task RunDiagnosticsCoreAsync(string platformId, IProgress<PlatformProgressEventArgs>? progress = null)
    {
        var provider = PlatformProviders.GetById(platformId);
        if (provider is null || provider.Domains.Count == 0)
            return;

        var primaryDomain = provider.Domains[0].Domain;
        var result = await NetworkDiagnostics.CheckHostAsync(primaryDomain, CancellationToken.None);

        if (!result.Success)
        {
            await _endpointCache.WarmupAsync(primaryDomain);
            result = await NetworkDiagnostics.CheckHostAsync(primaryDomain, CancellationToken.None);
        }

        var backendIp = _endpointCache.Describe(primaryDomain);
        var checkedAt = $"最后检测时间：{DateTime.Now:HH:mm:ss}";
        UpdatePlatform(platformId, platformStatuses.GetValueOrDefault(platformId) ?? TurboStatus.Stopped with
        {
            Detail = backendIp is null ? result.Detail : $"{backendIp} / {result.Detail}",
            Latency = result.Latency,
            LastChecked = checkedAt,
        });
        await AppLogger.InfoAsync($"{provider.Descriptor.Title} network test: {(result.Success ? "success" : "failed")}.");
    }

    internal static async Task<IpSelectionResult?> SelectBestIpAsync(string domain, string? previousIp)
    {
        var candidates = await ResolveCandidatesAsync(domain, previousIp);
        if (candidates.Count == 0)
        {
            await AppLogger.ErrorAsync($"DNS resolve failed for {domain}: no public IP candidates");
            return null;
        }

        // Multi-dimensional scoring: ping + TCP + HTTP response.
        // Candidates are probed concurrently (bounded by IpProbeGate) so that
        // one bad candidate cannot serialize the whole domain behind its timeouts.
        var tasks = candidates.Take(8).Select(async candidate =>
        {
            await IpProbeGate.WaitAsync();
            try
            {
                return await IpScoringService.ScoreIpAsync(domain, candidate.Ip);
            }
            finally
            {
                IpProbeGate.Release();
            }
        });

        var probed = await Task.WhenAll(tasks);
        var scores = probed.Where(s => s.IsReachable).ToList();

        if (scores.Count == 0)
        {
            await AppLogger.ErrorAsync($"No reachable IP for {domain}. Candidates: {string.Join(", ", candidates.Select(x => x.Ip))}");
            return null;
        }

        var ranked = scores.OrderByDescending(s => s.CompositeScore).ToList();
        var best = ranked.First();

        if (previousIp is not null)
        {
            var existing = ranked.FirstOrDefault(s => s.Ip == previousIp);
            if (existing is not null && existing.CompositeScore >= best.CompositeScore * 0.8)
            {
                best = existing;
            }
        }

        await AppLogger.InfoAsync($"Selected {best.Ip} for {domain} (score {best.CompositeScore:F1}/100) | {best.Summary}");
        return new IpSelectionResult(domain, best.Ip, ranked.First().Ip == best.Ip ? "Multi-dim best" : "Multi-dim (kept existing)", $"{best.CompositeScore:F0}");
    }

    internal static async Task<IReadOnlyList<ResolvedIpCandidate>> ResolveCandidatesAsync(string domain, string? previousIp)
    {
        await DnsResolutionGate.WaitAsync();
        try
        {
            var communityIps = await ResolveViaCommunityAsync(domain);

            var sourceTasks = new List<Task<IReadOnlyList<ResolvedIpCandidate>>>
            {
                ResolveViaSystemDnsAsync(domain),
            };
            sourceTasks.AddRange(DohEndpoints.Select(endpoint => ResolveViaDohSourceAsync(domain, endpoint)));
            var sourceResults = (await Task.WhenAll(sourceTasks)).ToList();

            if (!string.IsNullOrWhiteSpace(previousIp) && IPAddress.TryParse(previousIp, out var address)
                && (IpAddressHelpers.IsUsablePublicIpv4(address) || IsUsablePublicIpv6(address)))
                sourceResults.Insert(0, [new ResolvedIpCandidate(previousIp, "Existing hosts")]);

            if (communityIps.Count > 0)
                sourceResults.Insert(0, communityIps);

            return IpSelectionHelpers.Interleave(sourceResults, 8);
        }
        finally
        {
            DnsResolutionGate.Release();
        }
    }

    static async Task<IReadOnlyList<ResolvedIpCandidate>> ResolveViaCommunityAsync(string domain)
    {
        try
        {
            var entries = await GetCommunityIpsCachedAsync();
            return entries
                .Where(e => e.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase))
                .Select(e => new ResolvedIpCandidate(e.Ip, $"Community ({e.Source})"))
                .DistinctBy(x => x.Ip, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            await AppLogger.ErrorAsync($"Community IP fetch failed for {domain}: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Community IP list is fetched at most once per process (cached as a shared task).
    /// Previously every domain triggered a full hosts-file download (10s timeout),
    /// which dominated DNS phase wall time with 24+ concurrent fetches.
    /// </summary>
    static Task<IReadOnlyList<CommunityIpEntry>> GetCommunityIpsCachedAsync()
    {
        var cached = _communityCacheTask;
        if (cached is not null)
            return cached;

        lock (_communityCacheLock)
        {
            cached = _communityCacheTask;
            if (cached is not null)
                return cached;

            var allDomains = PlatformProviders.All
                .SelectMany(p => p.Domains.Select(d => d.Domain))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _communityCacheTask = FetchAndCacheAsync(allDomains);
            return _communityCacheTask;
        }
    }

    static async Task<IReadOnlyList<CommunityIpEntry>> FetchAndCacheAsync(HashSet<string> allDomains)
    {
        IReadOnlyList<CommunityIpEntry> fetched;
        try
        {
            fetched = await CommunityIpService.FetchGitHubIpsAsync(allDomains);
        }
        catch
        {
            lock (_communityCacheLock)
            {
                _communityCacheTask = null;
            }
            return [];
        }
        return fetched;
    }

    static async Task<IReadOnlyList<ResolvedIpCandidate>> ResolveViaSystemDnsAsync(string domain)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var addresses = await Dns.GetHostAddressesAsync(domain, cts.Token);
            return addresses
                .Where(a => IpAddressHelpers.IsUsablePublicIpv4(a) || IsUsablePublicIpv6(a))
                .Select(address => new ResolvedIpCandidate(address.ToString(),
                    address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "System DNS (IPv6)" : "System DNS"))
                .DistinctBy(x => x.Ip, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            await AppLogger.ErrorAsync($"System DNS failed for {domain}: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    static bool IsUsablePublicIpv6(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return false;
        if (address.Equals(IPAddress.IPv6Loopback)) return false;
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) return false;
        return true;
    }

    static async Task<IReadOnlyList<ResolvedIpCandidate>> ResolveViaDohSourceAsync(string domain, DohEndpoint endpoint)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var ips = await ResolveViaDohAsync(domain, endpoint.Template, cts.Token);
            return ips.Select(ip => new ResolvedIpCandidate(ip, endpoint.Name)).ToArray();
        }
        catch (Exception ex)
        {
            await AppLogger.ErrorAsync($"{endpoint.Name} failed for {domain}: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    // Domestic resolvers only: Cloudflare/Google DoH are unreliable on mainland networks.
    static readonly DohEndpoint[] DohEndpoints =
    [
        new("DNSPod DoH", "https://doh.pub/dns-query?name={0}&type=A"),
        new("DNSPod DoH (AAAA)", "https://doh.pub/dns-query?name={0}&type=AAAA"),
        new("AliDNS", "https://dns.alidns.com/resolve?name={0}&type=A"),
        new("AliDNS (AAAA)", "https://dns.alidns.com/resolve?name={0}&type=AAAA"),
    ];

    static async Task<IReadOnlyList<string>> ResolveViaDohAsync(string domain, string endpoint, CancellationToken cancellationToken)
    {
        var uri = string.Format(endpoint, Uri.EscapeDataString(domain));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/dns-json");
        using var response = await DohClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var ips = new List<string>();

        if (doc.RootElement.TryGetProperty("Answer", out var answers))
        {
            foreach (var answer in answers.EnumerateArray())
            {
                if (answer.TryGetProperty("type", out var type) && (type.GetInt32() == 1 || type.GetInt32() == 28) &&
                    answer.TryGetProperty("data", out var data))
                {
                    var ip = data.GetString();
                    if (!string.IsNullOrEmpty(ip) && IPAddress.TryParse(ip, out var addr) &&
                        (IsUsablePublicIpv4(addr) || IsUsablePublicIpv6(addr)))
                        ips.Add(ip);
                }
            }
        }

        return ips;
    }

    static bool IsUsablePublicIpv4(IPAddress address) => IpAddressHelpers.IsUsablePublicIpv4(address);

    static TurboStatus BuildRunningStatus(IPlatformProvider provider, string detail)
        => new(true, $"{provider.Descriptor.Title} 加速已开启", detail, "本地代理", "未检测", "可执行网络检测以验证连通性。");

    static TurboStatus BuildConflictStatus(IPlatformProvider provider, IReadOnlyList<HostsConflictEntry> conflicts)
    {
        var samples = string.Join(", ", conflicts
            .Take(3)
            .Select(x => $"{x.Domain} -> {x.Ip}"));
        var suffix = conflicts.Count > 3 ? $" 等 {conflicts.Count} 条" : string.Empty;
        var detail = conflicts.Any(x => x.IsLoopbackOrPrivate)
            ? $"检测到外部 hosts 条目正在拦截 {provider.Descriptor.Title} 域名：{samples}{suffix}。请先清理 Steam++ 或其他加速器写入的记录。"
            : $"检测到外部 hosts 条目覆盖了 {provider.Descriptor.Title} 域名：{samples}{suffix}。请先清理冲突记录后再启动加速。";
        return new TurboStatus(false, "存在 hosts 冲突", detail, "需处理", "未检测", "可清理冲突后重新检测。");
    }

    void UpdatePlatform(string platformId, TurboStatus status)
    {
        platformStatuses[platformId] = status;
        PlatformStatusChanged?.Invoke(this, new PlatformStatusChangedEventArgs(platformId, status));
    }

    public void Dispose()
    {
        try
        {
            _proxyServer.StopAsync().GetAwaiter().GetResult();
        }
        catch { }
        _proxyServer.Dispose();
        _certManager.Dispose();
        _refreshService.Dispose();
        _proxyLock.Dispose();
        HostsFileLock.Dispose();
    }
}

public sealed record TurboStatus(bool IsRunning, string Title, string Detail, string SystemProxy, string Latency, string LastChecked)
{
    public static readonly TurboStatus Stopped = new(false, "未启动", "点击启动后，将通过本地代理加速访问。", "未修改", "未检测", "尚未执行网络检测。");
}

public sealed record DomainDiagnosticResult(string Host, string CurrentIp, bool Success, string Latency, string Detail);

public sealed record PlatformStatusChangedEventArgs(string PlatformId, TurboStatus Status);

sealed record DohEndpoint(string Name, string Template);

public sealed record ResolvedIpCandidate(string Ip, string Source);

sealed record IpSelectionResult(string Domain, string Ip, string Source, string Latency);

public sealed record PlatformProgressEventArgs(
    string PlatformId,
    string StepDescription,
    int ProgressPercent,
    int ProgressMax);
