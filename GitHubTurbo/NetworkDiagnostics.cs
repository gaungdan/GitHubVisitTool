using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace GitHubTurbo;

public static class NetworkDiagnostics
{
    public static Task<DiagnosticResult> CheckHostAsync(string host, CancellationToken cancellationToken)
        => CheckEndpointAsync(host, host, cancellationToken);

    public static Task<DiagnosticResult> CheckIpForHostAsync(string host, string ip, CancellationToken cancellationToken)
        => CheckEndpointAsync(host, ip, cancellationToken);

    static async Task<DiagnosticResult> CheckEndpointAsync(string host, string connectTarget, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));

            using var client = new TcpClient { NoDelay = true };
            if (IPAddress.TryParse(connectTarget, out var address))
                await client.ConnectAsync(address, 443, timeoutCts.Token);
            else
                await client.ConnectAsync(connectTarget, 443, timeoutCts.Token);

            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(host).WaitAsync(timeoutCts.Token);

            var requestBytes = Encoding.ASCII.GetBytes(
                $"HEAD / HTTP/1.1\r\nHost: {host}\r\nUser-Agent: GitHubTurbo/1.0\r\nConnection: close\r\n\r\n");
            await ssl.WriteAsync(requestBytes, timeoutCts.Token);
            await ssl.FlushAsync(timeoutCts.Token);

            var buffer = new byte[1024];
            var read = await ssl.ReadAsync(buffer, timeoutCts.Token);
            watch.Stop();

            if (read > 0)
            {
                var response = Encoding.ASCII.GetString(buffer, 0, read);
                var statusLine = response.Split('\r')[0];
                return new DiagnosticResult(true, $"{watch.ElapsedMilliseconds} ms", $"{host} {statusLine}");
            }

            return new DiagnosticResult(true, $"{watch.ElapsedMilliseconds} ms", $"{host} TLS 连接成功。");
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return new DiagnosticResult(false, "超时", $"{host} 连接超时。");
        }
        catch (System.Security.Authentication.AuthenticationException ex)
        {
            watch.Stop();
            return new DiagnosticResult(false, "TLS失败", $"{host} TLS 握手失败：{ex.GetType().Name}。");
        }
        catch (SocketException ex)
        {
            watch.Stop();
            return new DiagnosticResult(false, "不可用", $"{host} TCP 连接失败：{ex.SocketErrorCode}。");
        }
        catch (Exception ex)
        {
            watch.Stop();
            return new DiagnosticResult(false, "不可用", $"{host} 连接失败：{ex.GetType().Name}。");
        }
    }
}

public sealed record DiagnosticResult(bool Success, string Latency, string Detail);
