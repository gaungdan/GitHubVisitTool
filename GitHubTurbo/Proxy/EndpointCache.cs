using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace GitHubTurbo.Proxy;

/// <summary>
/// In-memory domain → IP table used by the MITM proxy at connect time.
/// Warmup/refesh use the full multi-dimensional scorer; the hot path only
/// reads the cache and falls back to a quick TCP probe when empty.
/// </summary>
public sealed class EndpointCache
{
    static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, TlsSniMode> _preferredSni = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string>? TryGetIps(string domain)
        => _entries.TryGetValue(domain, out var entry) && !entry.IsStale ? entry.Ips : null;

    public TlsSniMode GetPreferredSni(string domain)
        => _preferredSni.TryGetValue(domain, out var mode) ? mode : TlsSniMode.IpAddress;

    public void RememberSni(string domain, TlsSniMode mode)
        => _preferredSni[domain] = mode;

    public string? Describe(string domain)
        => _entries.TryGetValue(domain, out var entry) ? entry.Ips.FirstOrDefault() : null;

    /// <summary>
    /// Test hook: inject known-good IPs so the MITM hot path can run without DNS/scoring.
    /// </summary>
    internal void Seed(string domain, params string[] ips)
    {
        if (ips.Length == 0)
            throw new ArgumentException("At least one IP is required.", nameof(ips));
        _entries[domain] = new CacheEntry(ips, DateTime.UtcNow);
    }

    /// <summary>
    /// Full-score one domain and store the ranked IPs. Used by warmup and periodic refresh.
    /// </summary>
    public async Task WarmupAsync(string domain, string? previousIp = null)
    {
        try
        {
            var result = await AppController.SelectBestIpAsync(domain, previousIp);
            if (result is null)
                return;

            var candidates = await AppController.ResolveCandidatesAsync(domain, result.Ip);
            var ips = candidates
                .Select(c => c.Ip)
                .Where(ip => !string.Equals(ip, result.Ip, StringComparison.OrdinalIgnoreCase))
                .Prepend(result.Ip)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToArray();

            _entries[domain] = new CacheEntry(ips, DateTime.UtcNow);
            await AppLogger.InfoAsync($"Endpoint cache warmed for {domain}: {string.Join(", ", ips)}");
        }
        catch (Exception ex)
        {
            await AppLogger.ErrorAsync($"Endpoint cache warmup failed for {domain}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Fast path used by the proxy: return a usable IP list, probing quickly if the cache is cold.
    /// </summary>
    public async Task<IReadOnlyList<string>?> GetIpsForConnectAsync(string domain, CancellationToken cancellationToken)
    {
        var cached = TryGetIps(domain);
        if (cached is { Count: > 0 })
            return cached;

        var candidates = await AppController.ResolveCandidatesAsync(domain, null);
        if (candidates.Count == 0)
            return null;

        var probed = new List<string>();
        foreach (var candidate in candidates.Take(5))
        {
            if (await QuickTcpProbeAsync(candidate.Ip, cancellationToken))
                probed.Add(candidate.Ip);
            if (probed.Count >= 3)
                break;
        }

        if (probed.Count == 0)
            probed.AddRange(candidates.Take(3).Select(c => c.Ip));

        _entries[domain] = new CacheEntry(probed.ToArray(), DateTime.UtcNow);
        return probed;
    }

    static async Task<bool> QuickTcpProbeAsync(string ip, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ConnectTimeout);
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Parse(ip), 443, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    sealed record CacheEntry(string[] Ips, DateTime FetchedAt)
    {
        public bool IsStale => DateTime.UtcNow - FetchedAt > CacheTtl;
    }
}
