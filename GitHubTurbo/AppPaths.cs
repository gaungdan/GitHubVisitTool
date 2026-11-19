using System.IO;

namespace GitHubTurbo;

public static class AppPaths
{
    public static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GitHubTurbo");

    public static readonly string RuntimeDirectory = AppContext.BaseDirectory;
    public static readonly string LogDirectory = Path.Combine(RuntimeDirectory, "logs");
    public static readonly string SettingsPath = Path.Combine(DataDirectory, "settings.json");
    public static readonly string CertificateDirectory = Path.Combine(DataDirectory, "certs");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(CertificateDirectory);
    }
}
