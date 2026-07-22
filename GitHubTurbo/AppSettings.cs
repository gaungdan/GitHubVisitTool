using System.IO;
using System.Text.Json;

namespace GitHubTurbo;

public sealed record AppSettings(string UpstreamProxyUrl)
{
    public static readonly AppSettings Default = new(string.Empty);

    public static async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(AppPaths.SettingsPath))
            return Default;

        try
        {
            await using var stream = File.OpenRead(AppPaths.SettingsPath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, cancellationToken: cancellationToken) ?? Default;
        }
        catch (JsonException)
        {
            await AppLogger.ErrorAsync("Unable to read application settings.");
            return Default;
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        AppPaths.EnsureDirectories();
        var temporaryPath = $"{AppPaths.SettingsPath}.tmp";
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
        File.Move(temporaryPath, AppPaths.SettingsPath, overwrite: true);
    }
}
