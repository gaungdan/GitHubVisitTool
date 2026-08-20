using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using GitHubTurbo;

// Add reference to the GitHubTurbo project
// dotnet add reference ../GitHubTurbo/GitHubTurbo.csproj

namespace GitHubTurboTest;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("=== GitHub Turbo 连通性测试 ===\n");

        // Baseline test
        Console.WriteLine("--- 基线测试（未加速）---");
        await TestConnectivity();

        // Start acceleration
        Console.WriteLine("\n--- 启动 GitHub 加速 ---");
        var controller = new AppController();
        await controller.InitializeAsync();

        var progress = new Progress<PlatformProgressEventArgs>(e =>
        {
            Console.WriteLine($"  [{e.ProgressPercent}%] {e.StepDescription}");
        });

        await controller.StartPlatformAsync("github", progress);

        var status = controller.PlatformStatuses.GetValueOrDefault("github");
        Console.WriteLine($"\n加速状态: {status?.Title}");
        Console.WriteLine($"详情: {status?.Detail}");

        // Test connectivity after acceleration
        Console.WriteLine("\n--- 加速后连通性测试 ---");
        await TestConnectivity();

        // Test hosts file entries
        Console.WriteLine("\n--- Hosts 文件条目 ---");
        var entries = HostsFileService.ReadEntries("github");
        foreach (var entry in entries.Take(5))
        {
            Console.WriteLine($"  {entry.Value} {entry.Key}");
        }
        if (entries.Count > 5)
            Console.WriteLine($"  ... 共 {entries.Count} 条");

        controller.Dispose();
    }

    static async Task TestConnectivity()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // Test github.com
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var resp = await client.GetAsync("https://github.com");
            sw.Stop();
            Console.WriteLine($"  github.com      : HTTP {(int)resp.StatusCode} ({sw.ElapsedMilliseconds}ms) - {resp.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  github.com      : FAIL - {ex.GetType().Name}");
        }

        // Test api.github.com
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var resp = await client.GetAsync("https://api.github.com");
            sw.Stop();
            Console.WriteLine($"  api.github.com  : HTTP {(int)resp.StatusCode} ({sw.ElapsedMilliseconds}ms) - {resp.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  api.github.com  : FAIL - {ex.GetType().Name}");
        }

        // Test raw.githubusercontent.com
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var resp = await client.GetAsync("https://raw.githubusercontent.com");
            sw.Stop();
            Console.WriteLine($"  raw.githubusercontent.com : HTTP {(int)resp.StatusCode} ({sw.ElapsedMilliseconds}ms)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  raw.githubusercontent.com : FAIL - {ex.GetType().Name}");
        }

        // Test avatars.githubusercontent.com
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var resp = await client.GetAsync("https://avatars.githubusercontent.com");
            sw.Stop();
            Console.WriteLine($"  avatars.githubusercontent.com : HTTP {(int)resp.StatusCode} ({sw.ElapsedMilliseconds}ms)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  avatars.githubusercontent.com : FAIL - {ex.GetType().Name}");
        }
    }
}
