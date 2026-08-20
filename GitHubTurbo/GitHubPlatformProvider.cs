using Color = System.Windows.Media.Color;

namespace GitHubTurbo;

public sealed class GitHubPlatformProvider : IPlatformProvider
{
    public static readonly GitHubPlatformProvider Instance = new();

    public PlatformDescriptor Descriptor => new(
        "github", "GitHub", "GitHub 网站与 API", "GH", Color.FromRgb(30, 63, 104));

    public IReadOnlyList<PlatformDomain> Domains => _domains;

    public bool HasLegacyEntries => HostsFileService.HasLegacyEntries;

    static readonly PlatformDomain[] _domains =
    [
        new("主站", "github.com"),
        new("API", "api.github.com"),
        new("Gist", "gist.github.com"),
        new("下载", "codeload.github.com"),
        new("原始文件", "raw.githubusercontent.com"),
        new("对象存储", "objects.githubusercontent.com"),
        new("头像", "avatars.githubusercontent.com"),
        new("用户图片", "user-images.githubusercontent.com"),
        new("图片代理", "camo.githubusercontent.com"),
        new("新静态资源", "github.githubassets.com"),
        new("Release", "github-releases.githubusercontent.com"),
        new("容器镜像", "github-registry-files.githubusercontent.com"),
        new("Pages", "github.io"),
        new("文档", "docs.github.com"),
        new("支持", "support.github.com"),
        new("培训", "training.github.com"),
        new("课堂", "classroom.github.com"),
        new("社区", "github.community"),
        new("在线编辑器", "github.dev"),
        new("博客", "github.blog"),
        new("状态页", "githubstatus.com"),
        new("云存储", "github-cloud.s3.amazonaws.com"),
        new("静态加速", "github-com.s3.amazonaws.com"),
        new("Gist文件", "gist.githubusercontent.com"),
        new("桌面资源", "desktop.githubusercontent.com"),
        new("文件上传", "uploads.github.com"),
        new("媒体资源", "media.githubusercontent.com"),
        new("私有图片", "private-user-images.githubusercontent.com"),
        new("教育", "education.github.com"),
        new("商店", "shop.github.com"),
        new("Copilot", "copilot.github.com"),
        new("Copilot代理", "copilot-proxy.githubusercontent.com"),
        new("Alive", "alive.github.com"),
        new("Schema", "schema.github.com"),
        new("资源中心", "resources.github.com"),
        new("安全", "security.github.com"),
        new("导入", "import.github.com"),
    ];
}
