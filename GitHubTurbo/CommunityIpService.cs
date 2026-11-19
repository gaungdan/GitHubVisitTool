using System.IO;
using System.Net.Http;

namespace GitHubTurbo;

/// <summary>
/// Fetches community-maintained IP lists (ineo6/hosts) as a base data source.
/// Mirrors are tried from most China-reachable to least, with an embedded
/// seed list as the final fallback so first-run quality doesn't depend on
/// raw.githubusercontent.com being reachable.
/// </summary>
public static class CommunityIpService
{
    static readonly string[] SourceUrls =
    [
        "https://cdn.jsdelivr.net/gh/ineo6/hosts@master/hosts",
        "https://fastly.jsdelivr.net/gh/ineo6/hosts@master/hosts",
        "https://raw.githubusercontent.com/ineo6/hosts/master/hosts",
    ];

    const string SeedResourceName = "GitHubTurbo.community-ip-seed.txt";

    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>
    /// Fetches community IP list and extracts domain-to-IP mappings, then merges
    /// the embedded seed so platforms missing from community hosts (e.g. Antigravity)
    /// still get fallback candidates. Community entries win on conflicts.
    /// </summary>
    public static async Task<IReadOnlyList<CommunityIpEntry>> FetchGitHubIpsAsync(
        IReadOnlySet<string> targetDomains,
        CancellationToken cancellationToken = default)
    {
        var community = new List<CommunityIpEntry>();
        foreach (var url in SourceUrls)
        {
            try
            {
                using var response = await Client.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var entries = ParseHostsContent(content, targetDomains, CommunityIpSource.Ineo6IPv4);
                if (entries.Count > 0)
                {
                    await AppLogger.InfoAsync($"Community IP list loaded from {new Uri(url).Host} ({entries.Count} entries).");
                    community = entries.ToList();
                    break;
                }
            }
            catch (Exception ex)
            {
                await AppLogger.ErrorAsync($"Community IP mirror {url} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var seed = ParseHostsContent(LoadSeedContent(), targetDomains, CommunityIpSource.SeedIPv4);
        if (community.Count == 0 && seed.Count == 0)
        {
            await AppLogger.ErrorAsync("All community IP mirrors failed; embedded seed list empty.");
            return [];
        }

        if (community.Count == 0)
            await AppLogger.ErrorAsync("All community IP mirrors failed; using embedded seed list.");

        return MergeWithSeed(community, seed);
    }

    /// <summary>
    /// Community list first (higher priority), then seed-only domain/IP pairs.
    /// Dedupes exact domain+ip collisions so scoring is not double-weighted.
    /// </summary>
    static IReadOnlyList<CommunityIpEntry> MergeWithSeed(
        IReadOnlyList<CommunityIpEntry> community,
        IReadOnlyList<CommunityIpEntry> seed)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<CommunityIpEntry>(community.Count + seed.Count);

        foreach (var e in community)
        {
            if (seen.Add(e.Domain + "|" + e.Ip))
                merged.Add(e);
        }

        var extras = 0;
        foreach (var e in seed)
        {
            if (!seen.Add(e.Domain + "|" + e.Ip))
                continue;
            merged.Add(e);
            extras++;
        }

        if (extras > 0)
            _ = AppLogger.InfoAsync($"Embedded seed contributed {extras} extra candidates.");
        return merged;
    }

    static string LoadSeedContent()
    {
        try
        {
            var assembly = typeof(CommunityIpService).Assembly;
            using var stream = assembly.GetManifestResourceStream(SeedResourceName);
            if (stream is null)
                return string.Empty;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            AppLogger.ErrorAsync($"Embedded seed list unreadable: {ex.GetType().Name}: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Parses hosts file content, extracting only entries matching our target domains.
    /// Handles both IPv4 and IPv6 entries.
    /// </summary>
    internal static IReadOnlyList<CommunityIpEntry> ParseHostsContent(
        string content,
        IReadOnlySet<string> targetDomains,
        CommunityIpSource source = CommunityIpSource.SeedIPv4)
    {
        var entries = new List<CommunityIpEntry>();

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                continue;

            var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            var ip = parts[0];
            if (!System.Net.IPAddress.TryParse(ip, out var address))
                continue;

            // Only public IPs
            if (!IpAddressHelpers.IsUsablePublicIpv4(address) && !IsUsablePublicIpv6(address))
                continue;

            foreach (var host in parts.Skip(1))
            {
                var domain = host.Trim().TrimEnd('/');
                if (targetDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
                {
                    var tagged = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                        ? (source == CommunityIpSource.Ineo6IPv4 ? CommunityIpSource.Ineo6IPv6 : CommunityIpSource.SeedIPv6)
                        : source;
                    entries.Add(new CommunityIpEntry(domain, ip, tagged));
                }
            }
        }

        return entries;
    }

    static bool IsUsablePublicIpv6(System.Net.IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            return false;
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            return false;
        if (address.Equals(System.Net.IPAddress.IPv6Loopback))
            return false;
        // Check for documentation prefix (2001:db8::/32)
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
            return false;
        return true;
    }
}

public sealed record CommunityIpEntry(string Domain, string Ip, CommunityIpSource Source);

public enum CommunityIpSource
{
    Ineo6IPv4,
    Ineo6IPv6,
    SeedIPv4,
    SeedIPv6,
}
