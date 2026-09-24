namespace GitHubTurbo.Proxy;

/// <summary>
/// Outbound ClientHello SNI strategy. Fake SNI hides the real domain from
/// SNI-based filtering while the HTTP Host/:authority header still routes correctly.
/// </summary>
public enum TlsSniMode
{
    /// <summary>SNI = the real domain. Most compatible, easiest to filter.</summary>
    RealDomain,

    /// <summary>
    /// SNI = the destination IP. Typical Fastly/GitHub bypass.
    /// Note: .NET SslStream omits IP literals from the SNI extension, so this
    /// degrades to <see cref="None"/> on the wire — still useful as a first attempt.
    /// </summary>
    IpAddress,

    /// <summary>No SNI extension at all.</summary>
    None,
}

public static class TlsSniHelper
{
    public static readonly TlsSniMode[] PreferredOrder =
    [
        TlsSniMode.IpAddress,
        TlsSniMode.None,
        TlsSniMode.RealDomain,
    ];

    /// <summary>
    /// TargetHost value for SslClientAuthenticationOptions.
    /// Empty string suppresses the SNI extension.
    /// </summary>
    public static string CreateTargetHost(string domain, string ip, TlsSniMode mode) => mode switch
    {
        TlsSniMode.IpAddress => ip,
        TlsSniMode.None => string.Empty,
        _ => domain,
    };
}
