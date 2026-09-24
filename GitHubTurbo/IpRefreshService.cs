using GitHubTurbo.Proxy;

namespace GitHubTurbo;

/// <summary>
/// Background service that periodically re-probes IPs and refreshes the endpoint cache.
/// The MITM proxy reads that cache at connect time; hosts only holds loopback entries.
/// </summary>
public sealed class IpRefreshService : IDisposable
{
    readonly System.Threading.Timer _refreshTimer;
    readonly EndpointCache _endpointCache;
    int _isRunning;

    public IpRefreshService(EndpointCache endpointCache, TimeSpan? interval = null)
    {
        _endpointCache = endpointCache;
        var dueTime = TimeSpan.FromMinutes(5);
        var period = interval ?? TimeSpan.FromMinutes(30);
        _refreshTimer = new System.Threading.Timer(_ => _ = RefreshAllAsync(), null, dueTime, period);
    }

    public async Task RefreshAllAsync()
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
            return;

        try
        {
            await AppLogger.InfoAsync("IP refresh: starting periodic re-probe...");

            foreach (var provider in PlatformProviders.All)
            {
                if (!HostsFileService.HasEntries(provider.Descriptor.Id))
                    continue;

                foreach (var domain in provider.Domains.Select(d => d.Domain).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var previous = _endpointCache.Describe(domain);
                        await _endpointCache.WarmupAsync(domain, previous);
                    }
                    catch (Exception ex)
                    {
                        await AppLogger.ErrorAsync($"IP refresh failed for {domain}: {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    public void Dispose() => _refreshTimer.Dispose();
}
