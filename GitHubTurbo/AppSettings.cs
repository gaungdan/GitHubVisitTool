using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitHubTurbo;

public sealed class AppSettings
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    readonly List<string> _enabledPlatforms = new();

    public IReadOnlyList<string> EnabledPlatforms => _enabledPlatforms;

    public bool RestoreHostsOnExit { get; set; }

    public bool AutoRefreshOnLaunch { get; set; }

    public static async Task<AppSettings> LoadAsync()
    {
        try
        {
            AppPaths.EnsureDirectories();
            if (!File.Exists(AppPaths.SettingsPath))
            {
                var settings = new AppSettings();
                await settings.SaveAsync();
                return settings;
            }

            await using var stream = File.OpenRead(AppPaths.SettingsPath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            await AppLogger.ErrorAsync($"Load settings failed: {ex.GetType().Name}: {ex.Message}");
            return new AppSettings();
        }
    }

    public async Task SaveAsync()
    {
        try
        {
            AppPaths.EnsureDirectories();
            var tempPath = AppPaths.SettingsPath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, this, JsonOptions);
            }

            File.Move(tempPath, AppPaths.SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            await AppLogger.ErrorAsync($"Save settings failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void SetPlatformEnabled(string platformId, bool enabled)
    {
        _enabledPlatforms.RemoveAll(x => x.Equals(platformId, StringComparison.OrdinalIgnoreCase));

        if (enabled)
            _enabledPlatforms.Add(platformId);
    }
}
