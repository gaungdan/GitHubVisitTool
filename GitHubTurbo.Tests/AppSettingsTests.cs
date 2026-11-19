using System.Text.Json;

namespace GitHubTurbo.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void EnabledPlatforms_RoundTripsThroughJson()
    {
        var settings = new AppSettings();
        settings.SetPlatformEnabled("github", true);
        settings.SetPlatformEnabled("gitlab", true);

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.Contains("github", restored.EnabledPlatforms);
        Assert.Contains("gitlab", restored.EnabledPlatforms);
    }

    [Fact]
    public void SetPlatformEnabled_IsIdempotent()
    {
        var settings = new AppSettings();
        settings.SetPlatformEnabled("github", true);
        settings.SetPlatformEnabled("github", true);
        settings.SetPlatformEnabled("github", false);

        Assert.Empty(settings.EnabledPlatforms);
    }
}
