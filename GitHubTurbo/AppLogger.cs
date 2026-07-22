using System.IO;
using System.Text;

namespace GitHubTurbo;

public static class AppLogger
{
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static Task InfoAsync(string message) => WriteAsync("INFO", message);

    public static Task ErrorAsync(string message) => WriteAsync("ERROR", message);

    static async Task WriteAsync(string level, string message)
    {
        try
        {
            AppPaths.EnsureDirectories();
            var path = Path.Combine(AppPaths.LogDirectory, $"turbo-{DateTime.UtcNow:yyyy-MM-dd}.log");
            var line = $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}";
            await Gate.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(path, line, Encoding.UTF8);
            }
            finally
            {
                Gate.Release();
            }
        }
        catch
        {
            // Logging must never stop proxy start or recovery.
        }
    }
}
