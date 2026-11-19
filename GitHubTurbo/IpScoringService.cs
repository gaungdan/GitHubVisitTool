using System.Diagnostics;
using System.Net.Sockets;
using System.Net.Security;
using GitHubTurbo.Proxy;

namespace GitHubTurbo;

/// <summary>
/// Multi-dimensional IP scoring: latency, stability, and HTTP response time.
/// Replaces the simple "pick lowest TLS latency" approach.
/// </summary>
public static class IpScoringService
{
    /// <summary>
    /// Scores a candidate IP across multiple dimensions.
    /// Higher score = better IP.
    /// </summary>
    public static async Task<IpScore> ScoreIpAsync(
        string domain,
        string ip,
        CancellationToken cancellationToken = default)
    {
        // Probe the three dimensions concurrently — TCP and HTTP reuse the same
        // connection attempt, and ping is independent, so wall time ≈ slowest dimension.
        var pingTask = MeasurePingAsync(ip, cancellationToken);
        var tcpTask = MeasureTcpAsync(domain, ip, cancellationToken);
        var httpTask = MeasureHttpResponseAsync(domain, ip, cancellationToken);

        await Task.WhenAll(pingTask, tcpTask, httpTask);

        var pingScore = await pingTask;
        var tcpScore = await tcpTask;
        var httpScore = await httpTask;

        // Weighted composite score (0-100)
        // Latency matters most, but stability and HTTP responsiveness also count
        var composite = pingScore.Score * 0.35
                     + tcpScore.Score * 0.35
                     + httpScore.Score * 0.30;

        return new IpScore(
            ip,
            composite,
            pingScore,
            tcpScore,
            httpScore);
    }

    /// <summary>
    /// ICMP ping test - measures raw network latency.
    /// Single attempt with short timeout: a ping adds little ranking signal
    /// over TCP/HTTP, so we keep it fast (especially for unreachable IPs).
    /// </summary>
    static async Task<DimensionScore> MeasurePingAsync(string ip, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync(ip, 1000);
            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
            {
                var score = LatencyToScore(reply.RoundtripTime);
                return new DimensionScore(true, score, $"{reply.RoundtripTime}ms");
            }

            return DimensionScore.Failed("ICMP unreachable");
        }
        catch (Exception ex)
        {
            return DimensionScore.Failed($"ICMP error: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// TCP 443 connection test - measures TCP handshake latency.
    /// Single attempt with hard timeout so blackholed IPs fail fast.
    /// </summary>
    static async Task<DimensionScore> MeasureTcpAsync(string domain, string ip, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(2000);
        try
        {
            using var client = new TcpClient { NoDelay = true };
            var sw = Stopwatch.StartNew();
            await client.ConnectAsync(System.Net.IPAddress.Parse(ip), 443, timeoutCts.Token);
            sw.Stop();
            var score = LatencyToScore(sw.ElapsedMilliseconds);
            return new DimensionScore(true, score, $"{sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            return DimensionScore.Failed($"TCP error: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// HTTP HEAD request test - measures full TLS + HTTP response time.
    /// This is the most realistic measure of actual browsing experience.
    /// </summary>
    static async Task<DimensionScore> MeasureHttpResponseAsync(string domain, string ip, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(2500);
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(System.Net.IPAddress.Parse(ip), 443, timeoutCts.Token);

            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
                (_, _, _, _) => true); // Accept all certs for testing
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                // Mirror the proxy's preferred outbound path: no SNI.
                TargetHost = TlsSniHelper.CreateTargetHost(domain, ip, TlsSniMode.None),
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
            }, timeoutCts.Token);

            var request = $"HEAD / HTTP/1.1\r\nHost: {domain}\r\nUser-Agent: GitHubTurbo/2.0\r\nConnection: close\r\n\r\n";
            var bytes = System.Text.Encoding.ASCII.GetBytes(request);
            var sw = Stopwatch.StartNew();
            await ssl.WriteAsync(bytes, timeoutCts.Token);
            await ssl.FlushAsync(timeoutCts.Token);

            var buffer = new byte[256];
            var read = await ssl.ReadAsync(buffer, timeoutCts.Token);
            sw.Stop();

            if (read > 0)
            {
                var score = LatencyToScore(sw.ElapsedMilliseconds);
                return new DimensionScore(true, score, $"{sw.ElapsedMilliseconds}ms HTTP");
            }

            return DimensionScore.Failed("Empty HTTP response");
        }
        catch (Exception ex)
        {
            return DimensionScore.Failed($"HTTP error: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Converts latency in ms to a 0-100 score.
    /// <50ms = excellent (90-100), <100ms = good (70-90), <200ms = ok (40-70), >300ms = poor (0-40)
    /// </summary>
    static double LatencyToScore(double latencyMs) => latencyMs switch
    {
        <= 0 => 50,
        < 50 => 100 - (latencyMs * 0.2),
        < 100 => 90 - ((latencyMs - 50) * 0.4),
        < 200 => 70 - ((latencyMs - 100) * 0.3),
        < 300 => 40 - ((latencyMs - 200) * 0.3),
        _ => Math.Max(10, 10 - ((latencyMs - 300) * 0.01))
    };
}

/// <summary>
/// Complete scoring result for one IP across all dimensions.
/// </summary>
public sealed record IpScore(
    string Ip,
    double CompositeScore,
    DimensionScore Ping,
    DimensionScore Tcp,
    DimensionScore Http)
{
    public bool IsReachable => Ping.Success || Tcp.Success || Http.Success;

    public string Summary =>
        $"Composite: {CompositeScore:F1}/100 | Ping: {Ping.Detail} | TCP: {Tcp.Detail} | HTTP: {Http.Detail}";
}

/// <summary>
/// Score for a single measurement dimension.
/// </summary>
public sealed record DimensionScore(bool Success, double Score, string Detail)
{
    public static DimensionScore Failed(string reason) => new(false, 0, reason);
}
