namespace GitHubTurbo;

public static class GitHubHosts
{
    public static (string prefix, string domain)[] Domains
        => GitHubPlatformProvider.Instance.Domains
            .Select(d => (d.Label, d.Domain))
            .ToArray();

    public static IReadOnlyList<string> GetWhitelistDomainNames()
        => GitHubPlatformProvider.Instance.Domains
            .Select(d => d.Domain)
            .ToArray();
}
