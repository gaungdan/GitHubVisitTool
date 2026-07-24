using System.IO;

namespace GitHubTurbo;

public static class AppPaths
{
    public static readonly string RootDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GitHubTurbo");

    public static readonly string LogDirectory = Path.Combine(RootDirectory, "logs");
    public static readonly string BackupDirectory = Path.Combine(RootDirectory, "backups");
    public static readonly string SettingsPath = Path.Combine(RootDirectory, "settings.json");
    public static readonly string IpCachePath = Path.Combine(RootDirectory, "ip-cache.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(BackupDirectory);
    }
}
