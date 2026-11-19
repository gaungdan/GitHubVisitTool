using System.Net;

namespace GitHubTurbo.Tests;

public sealed class IpSelectionHelpersTests
{
    [Fact]
    public void Interleave_GivesEachSourceAFairChanceAndRemovesDuplicates()
    {
        IReadOnlyList<IReadOnlyList<ResolvedIpCandidate>> sources =
        [
            Candidates("System", "1.1.1.1", "1.1.1.2", "1.1.1.3"),
            Candidates("Cloudflare", "2.2.2.1", "1.1.1.1", "2.2.2.3"),
            Candidates("Google", "3.3.3.1", "3.3.3.2"),
            Candidates("Tencent", "4.4.4.1", "4.4.4.2"),
        ];

        var result = IpSelectionHelpers.Interleave(sources, 8);

        Assert.Equal(8, result.Count);
        Assert.Equal(
            ["1.1.1.1", "2.2.2.1", "3.3.3.1", "4.4.4.1"],
            result.Take(4).Select(x => x.Ip));
        Assert.Equal(result.Count, result.Select(x => x.Ip).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(new long[] { 20 }, 20)]
    [InlineData(new long[] { 30, 10 }, 20)]
    [InlineData(new long[] { 30, 10, 20 }, 20)]
    public void Median_ReturnsMiddleLatency(long[] values, long expected)
        => Assert.Equal(expected, IpSelectionHelpers.Median(values));

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("10.0.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("203.0.113.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    public void IsUsablePublicIpv4_FiltersNonPublicRanges(string value, bool expected)
        => Assert.Equal(expected, IpAddressHelpers.IsUsablePublicIpv4(IPAddress.Parse(value)));

    [Theory]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    public void IsUsablePublicIpv6_FiltersNonPublicRanges(string value, bool expected)
        => Assert.Equal(expected, IpAddressHelpers.IsUsablePublicIpv6(IPAddress.Parse(value)));

    static IReadOnlyList<ResolvedIpCandidate> Candidates(string source, params string[] ips)
        => ips.Select(ip => new ResolvedIpCandidate(ip, source)).ToArray();
}
