using System.Collections.Concurrent;

namespace GitHubTurbo;

/// <summary>
/// Background service that periodically re-probes IPs and updates hosts when better ones are found.
/// Runs on a timer without blocking the UI.
/// </summary>
public sealed class IpRefreshService : IDisposable
{
    readonly System.Threading.Timer _refreshTimer;
    readonly ConcurrentDictionary<string, DateTime> _lastProbe = new();
    int _isRunning;

    public IpRefreshService(TimeSpan? interval = null)
    {
        var dueTime = TimeSpan.FromMinutes(5); // First refresh 5 min after start
        var period = interval ?? TimeSpan.FromMinutes(30); // Then every 30 min
        _refreshTimer = new System.Threading.Timer(_ => _ = RefreshAllAsync(), null, dueTime, period);
    }

    /// <summary>
    /// Triggers a refresh of all currently managed platforms.
    /// Safe to call multiple times - concurrent calls are serialized.
    /// </summary>
    public async Task RefreshAllAsync()
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
            return; // Already running

        try
        {
            await AppLogger.InfoAsync("IP refresh: starting periodic re-probe...");

            foreach (var provider in PlatformProviders.All)
            {
                try
                {
                    await RefreshPlatformAsync(provider);
                }
                catch (Exception ex)
                {
                    await AppLogger.ErrorAsync($"IP refresh failed for {provider.Descriptor.Title}: {ex.Message}");
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    async Task RefreshPlatformAsync(IPlatformProvider provider)
    {
        var platformId = provider.Descriptor.Id;
        if (!HostsFileService.HasEntries(platformId))
            return;

        var previousIps = HostsFileService.ReadEntries(platformId);
        var updatedEntries = new Dictionary<string, string>(previousIps, StringComparer.OrdinalIgnoreCase);
        var updated = false;

        foreach (var domain in provider.Domains.Select(d => d.Domain).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            previousIps.TryGetValue(domain, out var currentIp);
            var bestIp = await ReProbeDomainAsync(domain, currentIp);
            if (bestIp is not null && bestIp != currentIp)
            {
                await AppLogger.InfoAsync($"IP refresh: {domain} → {bestIp} (was {currentIp})");
                updatedEntries[domain] = bestIp;
                updated = true;
            }
        }

        if (updated)
        {
            await AppController.HostsFileLock.WaitAsync();
            try
            {
                HostsFileService.TryApplyHosts(platformId, updatedEntries);
                await AppLogger.InfoAsync($"IP refresh: updated hosts for {provider.Descriptor.Title}");
            }
            finally
            {
                AppController.HostsFileLock.Release();
            }
        }
    }

    static async Task<string?> ReProbeDomainAsync(string domain, string? currentIp)
    {
        // Re-resolve candidates (now includes community IPs)
        var candidates = await AppController.ResolveCandidatesAsync(domain, currentIp);
        if (candidates.Count == 0) return null;

        // Score top candidates
        var scores = new List<IpScore>();
        foreach (var candidate in candidates.Take(5))
        {
            await AppController.IpProbeGate.WaitAsync();
            try
            {
                var score = await IpScoringService.ScoreIpAsync(domain, candidate.Ip);
                if (score.IsReachable)
                    scores.Add(score);
            }
            finally
            {
                AppController.IpProbeGate.Release();
            }
        }

        // Keep current IP if it's still competitive
        var ranked = scores.OrderByDescending(s => s.CompositeScore).ToList();
        if (currentIp is not null)
        {
            var current = ranked.FirstOrDefault(s => s.Ip == currentIp);
            if (current is not null && ranked.Count > 0 && current.CompositeScore >= ranked[0].CompositeScore * 0.8)
                return currentIp; // Still good enough, don't change
        }

        return ranked.FirstOrDefault()?.Ip;
    }

    public void Dispose() => _refreshTimer.Dispose();
}
