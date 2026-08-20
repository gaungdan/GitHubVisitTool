using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;

namespace GitHubTurbo;

public sealed class AppController : IDisposable
{
    static readonly SemaphoreSlim HostsFileLock = new(1, 1);
    static readonly SemaphoreSlim IpProbeGate = new(16, 16);
    static readonly SemaphoreSlim DnsResolutionGate = new(8, 8);
    static readonly HttpClient DohClient = new() { Timeout = TimeSpan.FromSeconds(4) };
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
                var status = BuildConflictStatus(provider, conflicts);
                UpdatePlatform(platformId, status);
                await AppLogger.ErrorAsync($"Conflicting hosts entries detected for {provider.Descriptor.Title}: {string.Join(", ", conflicts.Select(x => $"{x.Domain}={x.Ip}"))}");
                return;
            }

            var previousIps = HostsFileService.ReadEntries(platformId);
            progress?.Report(new(platformId, $"正在解析 {provider.Descriptor.Title} 域名…", 5, 100));

            var scannedIps = await ResolvePlatformIpsAsync(provider, previousIps, progress);
            var ips = MergeWithPreviousIps(provider, scannedIps, previousIps);
            if (ips.Count == 0)
            {
                UpdatePlatform(platformId, TurboStatus.Stopped with { Detail = "没有找到可用 IP，请检查 DNS 或网络出口。" });
                return;
            }

            if (scannedIps.Count == 0 && previousIps.Count > 0)
            {
                UpdatePlatform(platformId, (platformStatuses.GetValueOrDefault(platformId) ?? TurboStatus.Stopped) with
                {
                    Detail = "本次 IP 扫描失败，已保留原有 hosts 记录。",
                    LastChecked = $"最后扫描时间：{DateTime.Now:HH:mm:ss}",
                });
                await AppLogger.ErrorAsync($"IP refresh failed for {provider.Descriptor.Title}; existing hosts entries were preserved.");
                return;
            }

            progress?.Report(new(platformId, $"正在写入 {provider.Descriptor.Title} Hosts 记录…", 85, 100));

            await HostsFileLock.WaitAsync();
            try
            {
                var applied = HostsFileService.TryApplyHosts(platformId, ips);
                await AppLogger.InfoAsync($"Apply hosts for {provider.Descriptor.Title}: {applied.Message}");
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

            var newStatus = new TurboStatus(true, $"{provider.Descriptor.Title} 加速已开启",
                $"已筛选并写入 {ips.Count} 条 hosts 记录。", "已修改", "未检测", "可执行网络检测以验证连通性。");
            UpdatePlatform(platformId, newStatus);
            await AppLogger.InfoAsync($"{provider.Descriptor.Title} hosts acceleration enabled.");

            progress?.Report(new(platformId, $"正在检测 {provider.Descriptor.Title} 网络连通性…", 95, 100));
            await RunDiagnosticsCoreAsync(platformId, progress);
            progress?.Report(new(platformId, $"{provider.Descriptor.Title} 加速完成", 100, 100));
        }
        catch (Exception ex)
        {
            await AppLogger.ErrorAsync($"Start failed for {platformId}: {ex.GetType().Name}: {ex.Message}");
            UpdatePlatform(platformId, TurboStatus.Stopped with { Detail = ex.Message });
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
            finally
            {
                HostsFileLock.Release();
            }
        }
        catch (Exception ex)
        {
            await AppLogger.ErrorAsync($"Stop failed for {platformId}: {ex.GetType().Name}: {ex.Message}");
            UpdatePlatform(platformId, platformStatuses.GetValueOrDefault(platformId) ?? TurboStatus.Stopped with { Detail = ex.Message });
        }
    }

    public async Task StartAllAsync(IProgress<PlatformProgressEventArgs>? progress = null)
    {
        var tasks = PlatformProviders.All
            .Select(p => StartPlatformAsync(p.Descriptor.Id, progress))
            .ToArray();
        await Task.WhenAll(tasks);
    }

    public async Task StopAllAsync()
    {
        var tasks = PlatformProviders.All
            .Select(p => StopPlatformAsync(p.Descriptor.Id))
            .ToArray();
        await Task.WhenAll(tasks);
    }

    public async Task<HostsOperationResult> BackupHostsAsync()
    {
        await HostsFileLock.WaitAsync();
        try
        {
            var result = HostsFileService.BackupHosts();
            await AppLogger.InfoAsync($"Backup hosts: {result.Message}");
            return result;
        }
        finally
        {
            HostsFileLock.Release();
        }
    }

    public async Task<HostsOperationResult> RestoreLatestBackupAsync()
    {
        await HostsFileLock.WaitAsync();
        try
        {
            var result = HostsFileService.RestoreLatestBackup();
            await AppLogger.InfoAsync($"Restore hosts backup: {result.Message}");
            RefreshPlatformStatusesFromHosts();
            return result;
        }
        finally
        {
            HostsFileLock.Release();
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

        var hostsEntries = HostsFileService.ReadEntries(platformId);
        var hosts = PlatformDiagnostics.GetDiagnosticDomains(provider).Select(d => d.Domain).ToArray();
        var completedCount = 0;

        var tasks = hosts.Select(async host =>
        {
            hostsEntries.TryGetValue(host, out var currentIp);
            var result = currentIp is null
                ? await NetworkDiagnostics.CheckHostAsync(host, CancellationToken.None)
                : await NetworkDiagnostics.CheckIpForHostAsync(host, currentIp, CancellationToken.None);
            var detail = currentIp is null ? result.Detail : $"{currentIp} / {result.Detail}";
            var counted = Interlocked.Increment(ref completedCount);
            progress?.Report(new(platformId, $"正在检测 {host}… ({counted}/{hosts.Length})",
                (int)((double)completedCount / hosts.Length * 100), 100));
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

    async Task RunDiagnosticsCoreAsync(string platformId, IProgress<PlatformProgressEventArgs>? progress = null)
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

    async Task<Dictionary<string, string>> ResolvePlatformIpsAsync(
        IPlatformProvider provider,
        IReadOnlyDictionary<string, string> previousIps,
        IProgress<PlatformProgressEventArgs>? progress = null)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var domains = provider.Domains.Select(d => d.Domain).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var completedCount = 0;

        var tasks = domains.Select(async domain =>
        {
            previousIps.TryGetValue(domain, out var previousIp);
            var result = await SelectBestIpAsync(domain, previousIp);
            if (result is not null)
            {
                lock (dict)
                {
                    dict[domain] = result.Ip;
                }
            }
            var counted = Interlocked.Increment(ref completedCount);
            progress?.Report(new(provider.Descriptor.Id, $"正在探测 {domain}… ({counted}/{domains.Length})",
                (int)((double)counted / domains.Length * 80), 100));
        });

        await Task.WhenAll(tasks);
        return dict;
    }

    static async Task<IpSelectionResult?> SelectBestIpAsync(string domain, string? previousIp)
    {
        var candidates = await ResolveCandidatesAsync(domain, previousIp);
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
                var diagnostic = await NetworkDiagnostics.CheckTlsAsync(domain, candidate.Ip, CancellationToken.None);
                return new IpProbeResult(candidate, diagnostic);
            }
            finally
            {
                IpProbeGate.Release();
            }
        });

        var quickResults = await Task.WhenAll(probes);
        var finalists = quickResults
            .Where(x => x.Diagnostic.Success)
            .OrderBy(x => x.Diagnostic.ElapsedMilliseconds ?? long.MaxValue)
            .Take(3)
            .Select(x => x.Candidate)
            .ToArray();

        if (finalists.Length == 0)
        {
            await AppLogger.ErrorAsync($"No reachable IP for {domain}. Candidates: {string.Join(", ", candidates.Select(x => x.Ip))}");
            return null;
        }

        var finalResults = await Task.WhenAll(finalists.Select(candidate => ProbeFinalistAsync(domain, candidate)));
        var best = finalResults
            .Where(x => x.SuccessCount > 0)
            .OrderByDescending(x => x.SuccessCount)
            .ThenBy(x => x.MedianLatencyMs)
            .FirstOrDefault();

        if (best is null)
        {
            await AppLogger.ErrorAsync($"No stable IP for {domain}. Finalists: {string.Join(", ", finalists.Select(x => x.Ip))}");
            return null;
        }

        await AppLogger.InfoAsync($"Selected {best.Candidate.Ip} for {domain} via {best.Candidate.Source}, median {best.MedianLatencyMs} ms, success {best.SuccessCount}/2.");
        return new IpSelectionResult(domain, best.Candidate.Ip, best.Candidate.Source, $"{best.MedianLatencyMs} ms");
    }

    static async Task<FinalIpProbeResult> ProbeFinalistAsync(string domain, ResolvedIpCandidate candidate)
    {
        var results = new List<DiagnosticResult>(2);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await IpProbeGate.WaitAsync();
            try
            {
                results.Add(await NetworkDiagnostics.CheckIpForHostAsync(domain, candidate.Ip, CancellationToken.None));
            }
            finally
            {
                IpProbeGate.Release();
            }
        }

        var successfulLatencies = results
            .Where(x => x.Success && x.ElapsedMilliseconds.HasValue)
            .Select(x => x.ElapsedMilliseconds!.Value)
            .ToArray();
        return new FinalIpProbeResult(candidate, successfulLatencies.Length, IpSelectionHelpers.Median(successfulLatencies));
    }

    static async Task<IReadOnlyList<ResolvedIpCandidate>> ResolveCandidatesAsync(string domain, string? previousIp)
    {
        await DnsResolutionGate.WaitAsync();
        try
        {
            var sourceTasks = new List<Task<IReadOnlyList<ResolvedIpCandidate>>>
            {
                ResolveViaSystemDnsAsync(domain),
            };
            sourceTasks.AddRange(DohEndpoints.Select(endpoint => ResolveViaDohSourceAsync(domain, endpoint)));

            var sourceResults = (await Task.WhenAll(sourceTasks)).ToList();
            if (!string.IsNullOrWhiteSpace(previousIp) && IPAddress.TryParse(previousIp, out var address) && IsUsablePublicIpv4(address))
                sourceResults.Insert(0, [new ResolvedIpCandidate(previousIp, "Existing hosts")]);

            return IpSelectionHelpers.Interleave(sourceResults, 8);
        }
        finally
        {
            DnsResolutionGate.Release();
        }
    }

    static async Task<IReadOnlyList<ResolvedIpCandidate>> ResolveViaSystemDnsAsync(string domain)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var addresses = await Dns.GetHostAddressesAsync(domain, cts.Token);
            return addresses
                .Where(IsUsablePublicIpv4)
                .Select(address => new ResolvedIpCandidate(address.ToString(), "System DNS"))
                .DistinctBy(x => x.Ip, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            await AppLogger.ErrorAsync($"System DNS failed for {domain}: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    static async Task<IReadOnlyList<ResolvedIpCandidate>> ResolveViaDohSourceAsync(string domain, DohEndpoint endpoint)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var ips = await ResolveViaDohAsync(domain, endpoint.Template, cts.Token);
            return ips.Select(ip => new ResolvedIpCandidate(ip, endpoint.Name)).ToArray();
        }
        catch (Exception ex)
        {
            await AppLogger.ErrorAsync($"{endpoint.Name} failed for {domain}: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    static readonly DohEndpoint[] DohEndpoints =
    [
        new("Cloudflare DoH", "https://cloudflare-dns.com/dns-query?name={0}&type=A"),
        new("Google DoH", "https://dns.google/resolve?name={0}&type=A"),
        new("Tencent DoH", "https://doh.pub/dns-query?name={0}&type=A"),
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

    static bool IsUsablePublicIpv4(IPAddress address) => IpAddressHelpers.IsUsablePublicIpv4(address);

    static Dictionary<string, string> MergeWithPreviousIps(
        IPlatformProvider provider,
        IReadOnlyDictionary<string, string> scannedIps,
        IReadOnlyDictionary<string, string> previousIps)
    {
        var merged = scannedIps.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var domain in provider.Domains.Select(x => x.Domain).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!merged.ContainsKey(domain) && previousIps.TryGetValue(domain, out var previousIp))
                merged[domain] = previousIp;
        }

        return merged;
    }

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

    void RefreshPlatformStatusesFromHosts()
    {
        foreach (var provider in PlatformProviders.All)
        {
            var conflicts = HostsFileService.FindConflictingEntries(provider);
            if (conflicts.Count > 0)
            {
                UpdatePlatform(provider.Descriptor.Id, BuildConflictStatus(provider, conflicts));
                continue;
            }

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
        HostsFileLock.Dispose();
    }
}

public sealed record TurboStatus(bool IsRunning, string Title, string Detail, string SystemProxy, string Latency, string LastChecked)
{
    public static readonly TurboStatus Stopped = new(false, "未启动", "点击启动后，将修改 hosts 文件加速访问。", "未修改", "未检测", "尚未执行网络检测。");
}

public sealed record DomainDiagnosticResult(string Host, string CurrentIp, bool Success, string Latency, string Detail);

public sealed record PlatformStatusChangedEventArgs(string PlatformId, TurboStatus Status);

sealed record DohEndpoint(string Name, string Template);

public sealed record ResolvedIpCandidate(string Ip, string Source);

sealed record IpProbeResult(ResolvedIpCandidate Candidate, DiagnosticResult Diagnostic);

sealed record FinalIpProbeResult(ResolvedIpCandidate Candidate, int SuccessCount, long MedianLatencyMs);

sealed record IpSelectionResult(string Domain, string Ip, string Source, string Latency);

public sealed record PlatformProgressEventArgs(
    string PlatformId,
    string StepDescription,
    int ProgressPercent,
    int ProgressMax);
