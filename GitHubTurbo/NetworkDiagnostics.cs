using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace GitHubTurbo;

public static class NetworkDiagnostics
{
    public static async Task<DiagnosticResult> CheckHostAsync(string host, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient { NoDelay = true };
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(host, 443, timeoutCts.Token);
            watch.Stop();

            using var stream = client.GetStream();
            stream.ReadTimeout = 2000;
            stream.WriteTimeout = 2000;

            var requestBytes = System.Text.Encoding.ASCII.GetBytes(
                $"HEAD / HTTP/1.1\r\nHost: {host}\r\nUser-Agent: GitHubTurbo/0.2\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(requestBytes, timeoutCts.Token);
            await stream.FlushAsync(timeoutCts.Token);

            var buffer = new byte[1024];
            var read = await stream.ReadAsync(buffer, timeoutCts.Token);
            watch.Stop();

            if (read > 0)
            {
                var response = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
                var statusLine = response.Split('\r')[0];
                return new DiagnosticResult(true, $"{watch.ElapsedMilliseconds} ms", $"{host} {statusLine}");
            }

            return new DiagnosticResult(true, $"{watch.ElapsedMilliseconds} ms", $"{host} TCP 连接成功。");
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return new DiagnosticResult(false, "超时", $"{host} 连接超时。");
        }
        catch (Exception ex)
        {
            watch.Stop();
            return new DiagnosticResult(false, "不可用", $"{host} 连接失败：{ex.GetType().Name}。");
        }
    }
}

public sealed record DiagnosticResult(bool Success, string Latency, string Detail);
