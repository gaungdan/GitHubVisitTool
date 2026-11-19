namespace GitHubTurbo.Tests;

public sealed class HostsFileServiceTests
{
    static Dictionary<string, string> Loopback(params string[] domains)
        => domains.ToDictionary(d => d, _ => "127.0.0.1", StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void BuildWithPlatformBlock_MovesMatchingDomainsOutOfLegacyBlock()
    {
        string[] existing =
        [
            "127.0.0.1 localhost",
            "# GitHubTurbo Start",
            "1.2.3.4 github.com",
            "5.6.7.8 gitlab.com",
            "# GitHubTurbo End",
        ];

        var result = HostsFileService.BuildWithPlatformBlock(existing, "github", Loopback("github.com"));

        Assert.DoesNotContain(result, line => line.Contains("1.2.3.4 github.com"));
        Assert.Contains("5.6.7.8 gitlab.com", result);
        Assert.Contains("# GitHubTurbo Start", result);
        Assert.Contains("# GitHubTurbo End", result);
        Assert.Contains("# github Start", result);
        Assert.Contains("127.0.0.1 github.com", result);
        Assert.Contains("# github End", result);
    }

    [Fact]
    public void BuildWithPlatformBlock_RemovesLegacyBlockWhenEmpty()
    {
        string[] existing =
        [
            "# GitHubTurbo Start",
            "1.2.3.4 github.com",
            "# GitHubTurbo End",
        ];

        var result = HostsFileService.BuildWithPlatformBlock(existing, "github", Loopback("github.com"));

        Assert.DoesNotContain("# GitHubTurbo Start", result);
        Assert.DoesNotContain("# GitHubTurbo End", result);
        Assert.Contains("127.0.0.1 github.com", result);
    }

    [Fact]
    public void BuildWithPlatformBlock_PreservesUnmatchedHostsOnPartialLegacyLine()
    {
        string[] existing =
        [
            "# GitHubTurbo Start",
            "1.2.3.4 github.com api.github.com",
            "# GitHubTurbo End",
        ];

        var result = HostsFileService.BuildWithPlatformBlock(existing, "github", Loopback("github.com"));

        Assert.Contains("1.2.3.4 api.github.com", result);
        Assert.DoesNotContain(result, line => line.Contains("1.2.3.4 github.com"));
    }
}
