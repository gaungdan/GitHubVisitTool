using Color = System.Windows.Media.Color;

namespace GitHubTurbo;

public sealed class GitLabPlatformProvider : IPlatformProvider
{
    public static readonly GitLabPlatformProvider Instance = new();

    public PlatformDescriptor Descriptor => new(
        "gitlab", "GitLab", "GitLab 网站与 API", "GL", Color.FromRgb(226, 67, 41));

    public IReadOnlyList<PlatformDomain> Domains => _domains;

    static readonly PlatformDomain[] _domains =
    [
        new("主站", "gitlab.com"),
        new("API", "gitlab.com/api"),
        new("Pages", "gitlab.io"),
        new("Registry", "registry.gitlab.com"),
        new("Assets", "assets.gitlab-static.net"),
    ];
}
