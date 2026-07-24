using System.Net;
using System.Text.Json;

namespace GitHubTurbo;

public sealed class AppController : IDisposable
{
    readonly SemaphoreSlim operationLock = new(1, 1);
    static readonly SemaphoreSlim IpProbeGate = new(16, 16);
    readonly Dictionary<string, TurboStatus> platformStatuses = new();
    AppSettings settings = new();

    public IReadOnlyDictionary<string, TurboStatus> PlatformStatuses => platformStatuses;

    public AppSettings Settings => settings;

    public event EventHandler<PlatformStatusChangedEventArgs>? PlatformStatusChanged;

    public bool AnyRunning => platformStatuses.Values.Any(s => s.IsRunning);

    public bool RestoreHostsOnExit => settings.RestoreHostsOnExit;

    public async Task InitializeAsync()
    {
        AppPaths.EnsureDirectories();
        settings = await AppSettings.LoadAsync();
        await AppLogger.InfoAsync("GitHub Turbo started.");

        foreach (var provider in PlatformProviders.All)
        {
            var hasEntries = HostsFileService.HasEntries(provider.Descriptor.Id);
            var hasLegacyEntries = provider.Descriptor.Id.Equals("github", StringComparison.OrdinalIgnoreCase) &&
                HostsFileService.HasLegacyEntries;
            if (!hasEntries && !hasLegacyEntries)
            {
                platformStatuses[provider.Descriptor.Id] = TurboStatus.Stopped;
                continue;
            }

            var hasLoopbackEntries = HostsFileService.HasLoopbackEntries(provider.Descriptor.Id);
            platformStatuses[provider.Descriptor.Id] = hasLoopbackEntries
                ? new TurboStatus(false, "需刷新", "检测到旧本机代理 hosts 条目。点击启动将刷新为真实 IP。", "需刷新", "未检测", "可执行网络检测以验证连通性。")
                : hasLegacyEntries
                    ? new TurboStatus(false, "需刷新", "检测到旧版 GitHubTurbo hosts 条目。点击启动将刷新为分平台记录。", "需刷新", "未检测", "可执行网络检测以验证连通性。")
                : new TurboStatus(true, $"{provider.Descriptor.Title} 加速已开启", "hosts 文件已写入加速条目。", "已修改", "未检测", "可执行网络检测以验证连通性。");
        }

        if (settings.AutoRefreshOnLaunch)
        {
            foreach (var platformId in settings.EnabledPlatforms.ToArray())
                await StartPlatformAsync(platformId);
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
            await AppLogger.InfoAsync($"Start acceleration for {provider.Descriptor.Title}.");

            if (HostsFileService.HasEntries(platformId))
            {
                var removed = HostsFileService.TryRemoveHosts(platformId);
                await AppLogger.InfoAsync($"Refresh hosts before start: {removed.Message}");
                if (!removed.Success)
                {
                    UpdatePlatform(platformId, TurboStatus.Stopped with { Detail = removed.Message });
                    return;
                }
            }

            var ips = await ResolvePlatformIpsAsync(provider);
            if (ips.Count == 0)
            {
                UpdatePlatform(platformId, TurboStatus.Stopped with { Detail = "没有找到可用 IP，请检查 DNS 或网络出口。" });
                return;
            }

            var applied = HostsFileService.TryApplyHosts(platformId, ips);
            await AppLogger.InfoAsync($"Apply hosts for {provider.Descriptor.Title}: {applied.Message}");
            if (!applied.Success)
            {
                UpdatePlatform(platformId, TurboStatus.Stopped with { Detail = applied.Message });
                return;
            }

            settings.SetPlatformEnabled(platformId, true);
            await settings.SaveAsync();

            var newStatus = new TurboStatus(true, $"{provider.Descriptor.Title} 加速已开启",
                $"已筛选并写入 {ips.Count} 条 hosts 记录。", "已修改", "未检测", "可执行网络检测以验证连通性。");
            UpdatePlatform(platformId, newStatus);
            await AppLogger.InfoAsync($"{provider.Descriptor.Title} hosts acceleration enabled.");
            await RunDiagnosticsCoreAsync(platformId);
        }
        catch (Exception ex)
        {
            await AppLogger.ErrorAsync($"Start failed for {platformId}: {ex.GetType().Name}: {ex.Message}");
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
                settings.SetPlatformEnabled(platformId, false);
                await settings.SaveAsync();
                UpdatePlatform(platformId, TurboStatus.Stopped);
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
            await AppLogger.InfoAsync($"{provider.Descriptor.Title} hosts acceleration disabled.");
        }
        catch (Exception ex)
        {
            await AppLogger.ErrorAsync($"Stop failed for {platformId}: {ex.GetType().Name}: {ex.Message}");
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

    public async Task<HostsOperationResult> BackupHostsAsync()
    {
        await operationLock.WaitAsync();
        try
        {
            var result = HostsFileService.BackupHosts();
            await AppLogger.InfoAsync($"Backup hosts: {result.Message}");
            return result;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<HostsOperationResult> RestoreLatestBackupAsync()
    {
        await operationLock.WaitAsync();
        try
        {
            var result = HostsFileService.RestoreLatestBackup();
            await AppLogger.InfoAsync($"Restore hosts backup: {result.Message}");
            RefreshPlatformStatusesFromHosts();
            return result;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task SetRestoreHostsOnExitAsync(bool value)
    {
        settings.RestoreHostsOnExit = value;
        await settings.SaveAsync();
    }

    public async Task SetAutoRefreshOnLaunchAsync(bool value)
    {
        settings.AutoRefreshOnLaunch = value;
        await settings.SaveAsync();
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

            var hostsEntries = HostsFileService.ReadEntries(platformId);
            var hosts = PlatformDiagnostics.GetDiagnosticDomains(provider).Select(d => d.Domain).ToArray();

            var tasks = hosts.Select(async host =>
            {
                hostsEntries.TryGetValue(host, out var currentIp);
                var result = currentIp is null
                    ? await NetworkDiagnostics.CheckHostAsync(host, CancellationToken.None)
                    : await NetworkDiagnostics.CheckIpForHostAsync(host, currentIp, CancellationToken.None);
                var detail = currentIp is null ? result.Detail : $"{currentIp} / {result.Detail}";
                return new DomainDiagnosticResult(host, currentIp ?? "-", result.Success, result.Latency, detail);
            });

            var results = (await Task.WhenAll(tasks)).ToList();
            var successCount = results.Count(x => x.Success);
            var overall = results.FirstOrDefault(x => x.Success) ??
                new DomainDiagnosticResult(provider.Domains[0].Domain, "-", false, "不可用", "所有域名检测失败。");

            UpdatePlatform(platformId, platformStatuses.GetValueOrDefault(platformId) ?? TurboStatus.Stopped with
            {
                Detail = $"网络测试已完成，{successCount}/{results.Count} 个域名可用。",
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
        var entries = HostsFileService.ReadEntries(platformId);
        entries.TryGetValue(primaryDomain, out var currentIp);
        var result = currentIp is null
            ? await NetworkDiagnostics.CheckHostAsync(primaryDomain, CancellationToken.None)
            : await NetworkDiagnostics.CheckIpForHostAsync(primaryDomain, currentIp, CancellationToken.None);
        var checkedAt = $"最后检测时间：{DateTime.Now:HH:mm:ss}";
        UpdatePlatform(platformId, platformStatuses.GetValueOrDefault(platformId) ?? TurboStatus.Stopped with
        {
            Detail = currentIp is null ? result.Detail : $"{currentIp} / {result.Detail}",
            Latency = result.Latency,
            LastChecked = checkedAt,
        });
        await AppLogger.InfoAsync($"{provider.Descriptor.Title} network test: {(result.Success ? "success" : "failed")}.");
    }

    async Task<Dictionary<string, string>> ResolvePlatformIpsAsync(IPlatformProvider provider)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var domains = provider.Domains.Select(d => d.Domain).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var tasks = domains.Select(async domain =>
        {
            var result = await SelectBestIpAsync(domain);
            if (result is not null)
            {
                lock (dict)
                {
                    dict[domain] = result.Ip;
                }
            }
        });

        await Task.WhenAll(tasks);
        return dict;
    }

    static async Task<IpSelectionResult?> SelectBestIpAsync(string domain)
    {
        var candidates = await ResolveCandidatesAsync(domain);
        if (candidates.Count == 0)
        {
            await AppLogger.ErrorAsync($"DNS resolve failed for {domain}: no public IPv4 candidates");
            return null;
        }

        var probes = candidates.Take(8).Select(async candidate =>
        {
            await IpProbeGate.WaitAsync();
            try
            {
                var diagnostic = await NetworkDiagnostics.CheckIpForHostAsync(domain, candidate.Ip, CancellationToken.None);
                return new IpProbeResult(candidate, diagnostic, ParseLatencyMs(diagnostic.Latency));
            }
            finally
            {
                IpProbeGate.Release();
            }
        });

        var results = await Task.WhenAll(probes);
        var best = results
            .Where(x => x.Diagnostic.Success)
            .OrderBy(x => x.LatencyMs ?? int.MaxValue)
            .FirstOrDefault();

        if (best is null)
        {
            await AppLogger.ErrorAsync($"No reachable IP for {domain}. Candidates: {string.Join(", ", candidates.Select(x => x.Ip))}");
            return null;
        }

        await AppLogger.InfoAsync($"Selected {best.Candidate.Ip} for {domain} via {best.Candidate.Source}, latency {best.Diagnostic.Latency}.");
        return new IpSelectionResult(domain, best.Candidate.Ip, best.Candidate.Source, best.Diagnostic.Latency);
    }

    static async Task<IReadOnlyList<ResolvedIpCandidate>> ResolveCandidatesAsync(string domain)
    {
        var candidates = new Dictionary<string, ResolvedIpCandidate>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var addresses = await Dns.GetHostAddressesAsync(domain, cts.Token);
            foreach (var address in addresses.Where(IsUsablePublicIpv4))
                candidates.TryAdd(address.ToString(), new ResolvedIpCandidate(address.ToString(), "System DNS"));
        }
        catch (Exception ex)
        {
            await AppLogger.ErrorAsync($"System DNS failed for {domain}: {ex.GetType().Name}: {ex.Message}");
        }

        foreach (var endpoint in DohEndpoints)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var ips = await ResolveViaDohAsync(domain, endpoint.Template, cts.Token);
                foreach (var ip in ips)
                    candidates.TryAdd(ip, new ResolvedIpCandidate(ip, endpoint.Name));
            }
            catch (Exception ex)
            {
                await AppLogger.ErrorAsync($"{endpoint.Name} failed for {domain}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return candidates.Values.ToArray();
    }

    static readonly DohEndpoint[] DohEndpoints =
    [
        new("Cloudflare DoH", "https://cloudflare-dns.com/dns-query?name={0}&type=A"),
        new("Google DoH", "https://dns.google/resolve?name={0}&type=A"),
        new("Tencent DoH", "https://doh.pub/dns-query?name={0}&type=A"),
    ];

    static async Task<IReadOnlyList<string>> ResolveViaDohAsync(string domain, string endpoint, CancellationToken cancellationToken)
    {
        using var client = new System.Net.Http.HttpClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        var uri = string.Format(endpoint, Uri.EscapeDataString(domain));
        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/dns-json");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var ips = new List<string>();

        if (doc.RootElement.TryGetProperty("Answer", out var answers))
        {
            foreach (var answer in answers.EnumerateArray())
            {
                if (answer.TryGetProperty("type", out var type) && type.GetInt32() == 1 &&
                    answer.TryGetProperty("data", out var data))
                {
                    var ip = data.GetString();
                    if (!string.IsNullOrEmpty(ip) && IPAddress.TryParse(ip, out var addr) &&
                        IsUsablePublicIpv4(addr))
                        ips.Add(ip);
                }
            }
        }

        return ips;
    }

    static bool IsUsablePublicIpv4(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        var bytes = address.GetAddressBytes();
        if (address.Equals(IPAddress.Loopback) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.None))
            return false;

        if (bytes[0] == 10)
            return false;

        if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            return false;

        if (bytes[0] == 192 && bytes[1] == 168)
            return false;

        if (bytes[0] == 169 && bytes[1] == 254)
            return false;

        if (bytes[0] == 127)
            return false;

        return true;
    }

    static int? ParseLatencyMs(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && int.TryParse(parts[0], out var ms) ? ms : null;
    }

    void RefreshPlatformStatusesFromHosts()
    {
        foreach (var provider in PlatformProviders.All)
        {
            var hasEntries = HostsFileService.HasEntries(provider.Descriptor.Id);
            var status = hasEntries
                ? new TurboStatus(true, $"{provider.Descriptor.Title} 加速已开启", "hosts 文件已写入加速条目。", "已修改", "未检测", "可执行网络检测以验证连通性。")
                : TurboStatus.Stopped;
            UpdatePlatform(provider.Descriptor.Id, status);
        }
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

public sealed record DomainDiagnosticResult(string Host, string CurrentIp, bool Success, string Latency, string Detail);

public sealed record PlatformStatusChangedEventArgs(string PlatformId, TurboStatus Status);

sealed record DohEndpoint(string Name, string Template);

sealed record ResolvedIpCandidate(string Ip, string Source);

sealed record IpProbeResult(ResolvedIpCandidate Candidate, DiagnosticResult Diagnostic, int? LatencyMs);

sealed record IpSelectionResult(string Domain, string Ip, string Source, string Latency);
