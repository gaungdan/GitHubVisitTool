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
        "uploads.github.com",
        "media.githubusercontent.com",
        "private-user-images.githubusercontent.com",
        "education.github.com",
        "shop.github.com",
        "copilot.github.com",
        "copilot-proxy.githubusercontent.com",
        "alive.github.com",
        "schema.github.com",
        "resources.github.com",
        "security.github.com",
        "import.github.com",
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
