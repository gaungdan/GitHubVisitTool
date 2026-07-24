namespace GitHubTurbo;

public static class PlatformDiagnostics
{
    static readonly HashSet<string> GitHubCriticalDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "api.github.com",
        "gist.github.com",
        "codeload.github.com",
        "raw.githubusercontent.com",
        "objects.githubusercontent.com",
        "avatars.githubusercontent.com",
        "user-images.githubusercontent.com",
        "camo.githubusercontent.com",
        "github.githubassets.com",
        "github-releases.githubusercontent.com",
        "github-registry-files.githubusercontent.com",
        "github.io",
        "gist.githubusercontent.com",
        "desktop.githubusercontent.com",
        "github.blog",
        "githubstatus.com",
        "github-cloud.s3.amazonaws.com",
        "github-com.s3.amazonaws.com",
    };

    static readonly HashSet<string> GitLabCriticalDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gitlab.com",
        "registry.gitlab.com",
        "assets.gitlab-static.net",
        "gitlab.io",
    };

    public static IReadOnlyList<PlatformDomain> GetDiagnosticDomains(IPlatformProvider provider)
    {
        var critical = provider.Descriptor.Id.Equals("github", StringComparison.OrdinalIgnoreCase)
            ? GitHubCriticalDomains
            : provider.Descriptor.Id.Equals("gitlab", StringComparison.OrdinalIgnoreCase)
                ? GitLabCriticalDomains
                : null;

        var domains = provider.Domains;
        if (critical is null)
            return domains;

        var selected = domains.Where(d => critical.Contains(d.Domain)).ToArray();
        return selected.Length > 0 ? selected : domains;
    }
}
