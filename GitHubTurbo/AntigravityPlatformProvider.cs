using Color = System.Windows.Media.Color;

namespace GitHubTurbo;

public sealed class AntigravityPlatformProvider : IPlatformProvider
{
    public static readonly AntigravityPlatformProvider Instance = new();

    public PlatformDescriptor Descriptor => new(
        "antigravity", "Antigravity", "Google Antigravity 智能体开发平台", "AG", Color.FromRgb(79, 70, 229));

    public IReadOnlyList<PlatformDomain> Domains => _domains;

    static readonly PlatformDomain[] _domains =
    [
        new("主站", "antigravity.google"),
        new("WWW", "www.antigravity.google"),
        new("Web 应用", "app.antigravity.google"),
        new("备用入口", "antigravity.withgoogle.com"),
        new("Code Assist", "codeassist.google"),
        new("Hub 安装包", "storage.googleapis.com"),
        new("IDE 安装包", "edgedl.me.gvt1.com"),
        new("通用下载", "dl.google.com"),
        new("账号登录", "accounts.google.com"),
        new("OAuth", "oauth2.googleapis.com"),
        new("云代码 API", "cloudcode-pa.googleapis.com"),
        new("Gemini API", "generativelanguage.googleapis.com"),
        new("Google API", "www.googleapis.com"),
        new("API 前端", "apis.google.com"),
        new("静态资源", "www.gstatic.com"),
        new("SSL 资源", "ssl.gstatic.com"),
        new("字体样式", "fonts.googleapis.com"),
        new("字体文件", "fonts.gstatic.com"),
        new("用户图片", "lh3.googleusercontent.com"),
    ];
}
