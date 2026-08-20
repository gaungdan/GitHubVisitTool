using System.IO;
using System.Text;

namespace GitHubTurbo;

public static class HostsFileService
{
    const string HostsPath = @"C:\Windows\System32\drivers\etc\hosts";
    const long MaxFileLength = 52428800;

    static readonly Encoding NoBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    internal const string LegacyMarkStart = "# GitHubTurbo Start";
    internal const string LegacyMarkEnd = "# GitHubTurbo End";

    public static string GetMarkerStart(string platformId) => $"# {platformId} Start";
    public static string GetMarkerEnd(string platformId) => $"# {platformId} End";

    public static bool HasAnyEntries
    {
        get
        {
            if (!File.Exists(HostsPath))
                return false;

            var content = File.ReadAllText(HostsPath, NoBomUtf8);
            return content.Contains(LegacyMarkEnd, StringComparison.OrdinalIgnoreCase) ||
                PlatformProviders.All.Any(p => content.Contains(GetMarkerEnd(p.Descriptor.Id), StringComparison.OrdinalIgnoreCase));
        }
    }

    public static bool HasEntries(string platformId)
    {
        if (!File.Exists(HostsPath))
            return false;

        var content = File.ReadAllText(HostsPath, NoBomUtf8);
        return content.Contains(GetMarkerStart(platformId), StringComparison.OrdinalIgnoreCase) ||
            content.Contains(GetMarkerEnd(platformId), StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasLegacyEntries =>
        File.Exists(HostsPath) &&
        File.ReadAllText(HostsPath, NoBomUtf8).Contains(LegacyMarkEnd, StringComparison.OrdinalIgnoreCase);

    public static bool HasLoopbackEntries(string platformId)
    {
        if (!File.Exists(HostsPath))
            return false;

        var lines = File.ReadAllLines(HostsPath, NoBomUtf8);
        var markerStart = GetMarkerStart(platformId);
        var markerEnd = GetMarkerEnd(platformId);
        var inBlock = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Equals(markerStart, StringComparison.OrdinalIgnoreCase))
            {
                inBlock = true;
                continue;
            }

            if (trimmed.Equals(markerEnd, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!inBlock)
                continue;

            if (trimmed.StartsWith("127.0.0.1 ", StringComparison.Ordinal) ||
                trimmed.StartsWith("::1 ", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static IReadOnlyDictionary<string, string> ReadEntries(string platformId)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(HostsPath))
            return dict;

        var markerStart = GetMarkerStart(platformId);
        var markerEnd = GetMarkerEnd(platformId);
        var inBlock = false;

        foreach (var line in File.ReadAllLines(HostsPath, NoBomUtf8))
        {
            var trimmed = line.Trim();
            if (trimmed.Equals(markerStart, StringComparison.OrdinalIgnoreCase))
            {
                inBlock = true;
                continue;
            }

            if (trimmed.Equals(markerEnd, StringComparison.OrdinalIgnoreCase))
                break;

            if (!inBlock || string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                continue;

            var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                dict[parts[1]] = parts[0];
        }

        return dict;
    }

    public static IReadOnlyList<HostsConflictEntry> FindConflictingEntries(IPlatformProvider provider)
    {
        var conflicts = new List<HostsConflictEntry>();
        if (!File.Exists(HostsPath))
            return conflicts;

        var managedStarts = PlatformProviders.All
            .Select(p => GetMarkerStart(p.Descriptor.Id))
            .Append(LegacyMarkStart)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var managedEnds = PlatformProviders.All
            .Select(p => GetMarkerEnd(p.Descriptor.Id))
            .Append(LegacyMarkEnd)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trackedDomains = provider.Domains
            .Select(d => d.Domain)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var inManagedBlock = false;
        var lineNumber = 0;
        foreach (var line in File.ReadAllLines(HostsPath, NoBomUtf8))
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (managedStarts.Contains(trimmed))
            {
                inManagedBlock = true;
                continue;
            }

            if (managedEnds.Contains(trimmed))
            {
                inManagedBlock = false;
                continue;
            }

            if (inManagedBlock || string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                continue;

            var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            var ip = parts[0];
            foreach (var host in parts.Skip(1))
            {
                if (!trackedDomains.Contains(host))
                    continue;

                conflicts.Add(new HostsConflictEntry(host, ip, lineNumber, IpAddressHelpers.IsPrivateOrLoopback(ip)));
            }
        }

        return conflicts;
    }

    public static void EnsureDefaultHosts()
    {
        var fileInfo = new FileInfo(HostsPath);
        if (fileInfo.Exists)
            return;

        using var stream = fileInfo.Create();
        using var writer = new StreamWriter(stream, NoBomUtf8);
        writer.WriteLine("# Copyright (c) 1993-2009 Microsoft Corp.");
        writer.WriteLine("#");
        writer.WriteLine("# This is a sample HOSTS file used by Microsoft TCP/IP for Windows.");
        writer.WriteLine("#");
        writer.WriteLine("# This file contains the mappings of IP addresses to host names. Each");
        writer.WriteLine("# entry should be kept on an individual line. The IP address should");
        writer.WriteLine("# be placed in the first column followed by the corresponding host name.");
        writer.WriteLine("# The IP address and the host name should be separated by at least one");
        writer.WriteLine("# space.");
        writer.WriteLine("#");
        writer.WriteLine("# localhost name resolution is handled within DNS itself.");
        writer.WriteLine("#\t127.0.0.1       localhost");
        writer.WriteLine("#\t::1             localhost");
    }

    public static bool ApplyHosts(string platformId, IReadOnlyDictionary<string, string> domainToIp)
        => TryApplyHosts(platformId, domainToIp).Success;

    public static HostsOperationResult TryApplyHosts(string platformId, IReadOnlyDictionary<string, string> domainToIp)
        => MutateHosts(existing => BuildWithPlatformBlock(existing, platformId, domainToIp), flushDns: true);

    public static bool RemoveHosts(string platformId) => TryRemoveHosts(platformId).Success;

    public static HostsOperationResult TryRemoveHosts(string platformId)
        => MutateHosts(existing => RemovePlatformBlock(existing, platformId), flushDns: true);

    public static bool RemoveAllHosts() => TryRemoveAllHosts().Success;

    public static HostsOperationResult TryRemoveAllHosts()
        => MutateHosts(RemoveAllKnownBlocks, flushDns: true);

    public static HostsOperationResult BackupHosts()
    {
        try
        {
            if (!File.Exists(HostsPath))
                EnsureDefaultHosts();

            AppPaths.EnsureDirectories();
            var backupPath = CreateBackupPath();
            File.Copy(HostsPath, backupPath, overwrite: true);
            return HostsOperationResult.Ok($"hosts 已备份到 {backupPath}", backupPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            return HostsOperationResult.Fail($"权限不足，无法备份 hosts：{ex.Message}");
        }
        catch (IOException ex)
        {
            return HostsOperationResult.Fail($"hosts 文件被占用或不可读：{ex.Message}");
        }
        catch (Exception ex)
        {
            return HostsOperationResult.Fail($"备份 hosts 失败：{ex.Message}");
        }
    }

    public static HostsOperationResult RestoreLatestBackup()
    {
        try
        {
            var latest = ListBackups().FirstOrDefault();
            if (latest is null)
                return HostsOperationResult.Fail("没有可恢复的 hosts 备份。");

            var fileInfo = PrepareHostsFile();
            var wasReadOnly = fileInfo.IsReadOnly;
            if (!TryClearReadOnly(fileInfo, out var readOnlyError))
                return HostsOperationResult.Fail(readOnlyError);

            try
            {
                var currentBackup = BackupHosts();
                if (!currentBackup.Success)
                    return currentBackup;

                ReplaceHostsAtomically(File.ReadAllLines(latest, NoBomUtf8));
                var flush = FlushDnsCache();
                return flush.Success
                    ? HostsOperationResult.Ok($"已从 {latest} 恢复 hosts。", latest)
                    : HostsOperationResult.Ok($"已恢复 hosts，但刷新 DNS 缓存失败：{flush.Message}", latest);
            }
            finally
            {
                RestoreReadOnly(fileInfo, wasReadOnly);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            return HostsOperationResult.Fail($"权限不足，无法恢复 hosts：{ex.Message}");
        }
        catch (IOException ex)
        {
            return HostsOperationResult.Fail($"恢复 hosts 失败，文件可能被占用：{ex.Message}");
        }
        catch (Exception ex)
        {
            return HostsOperationResult.Fail($"恢复 hosts 失败：{ex.Message}");
        }
    }

    public static IReadOnlyList<string> ListBackups()
    {
        if (!Directory.Exists(AppPaths.BackupDirectory))
            return [];

        return Directory.GetFiles(AppPaths.BackupDirectory, "hosts-*.bak")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
    }

    static HostsOperationResult MutateHosts(Func<IReadOnlyList<string>, IReadOnlyList<string>> mutate, bool flushDns)
    {
        try
        {
            var fileInfo = PrepareHostsFile();
            if (fileInfo.Length > MaxFileLength)
                return HostsOperationResult.Fail("hosts 文件过大，已停止写入以避免误操作。");

            var wasReadOnly = fileInfo.IsReadOnly;
            if (!TryClearReadOnly(fileInfo, out var readOnlyError))
                return HostsOperationResult.Fail(readOnlyError);

            try
            {
                var existing = File.ReadAllLines(HostsPath, NoBomUtf8);
                var updated = mutate(existing);
                var backup = BackupHosts();
                if (!backup.Success)
                    return backup;

                ReplaceHostsAtomically(updated);
                if (!flushDns)
                    return HostsOperationResult.Ok("hosts 写入成功。", backup.Path);

                var flush = FlushDnsCache();
                return flush.Success
                    ? HostsOperationResult.Ok("hosts 写入成功，DNS 缓存已刷新。", backup.Path)
                    : HostsOperationResult.Ok($"hosts 写入成功，但刷新 DNS 缓存失败：{flush.Message}", backup.Path);
            }
            finally
            {
                RestoreReadOnly(fileInfo, wasReadOnly);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            return HostsOperationResult.Fail($"权限不足，请以管理员身份运行：{ex.Message}");
        }
        catch (IOException ex)
        {
            return HostsOperationResult.Fail($"hosts 文件被占用或写入失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            return HostsOperationResult.Fail($"hosts 操作失败：{ex.Message}");
        }
    }

    static FileInfo PrepareHostsFile()
    {
        if (!File.Exists(HostsPath))
            EnsureDefaultHosts();

        return new FileInfo(HostsPath);
    }

    static bool TryClearReadOnly(FileInfo fileInfo, out string error)
    {
        error = string.Empty;
        if (!fileInfo.IsReadOnly)
            return true;

        try
        {
            fileInfo.IsReadOnly = false;
            return true;
        }
        catch (Exception ex)
        {
            error = $"hosts 文件为只读，且无法取消只读属性：{ex.Message}";
            return false;
        }
    }

    static void RestoreReadOnly(FileInfo fileInfo, bool wasReadOnly)
    {
        if (!wasReadOnly)
            return;

        try { fileInfo.IsReadOnly = true; }
        catch { }
    }

    static IReadOnlyList<string> BuildWithPlatformBlock(
        IReadOnlyList<string> existing,
        string platformId,
        IReadOnlyDictionary<string, string> domainToIp)
    {
        var result = RemovePlatformBlock(existing, platformId).ToList();
        if (result.Count > 0 && !string.IsNullOrWhiteSpace(result[^1]))
            result.Add(string.Empty);

        result.Add(GetMarkerStart(platformId));
        foreach (var kv in domainToIp.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            result.Add($"{kv.Value} {kv.Key}");
        result.Add(GetMarkerEnd(platformId));
        return result;
    }

    static IReadOnlyList<string> RemovePlatformBlock(IReadOnlyList<string> existing, string platformId)
    {
        var result = new List<string>();
        var markerStart = GetMarkerStart(platformId);
        var markerEnd = GetMarkerEnd(platformId);
        var inBlock = false;

        foreach (var line in existing)
        {
            var trimmed = line.Trim();
            if (trimmed.Equals(markerStart, StringComparison.OrdinalIgnoreCase))
            {
                inBlock = true;
                continue;
            }

            if (trimmed.Equals(markerEnd, StringComparison.OrdinalIgnoreCase))
            {
                inBlock = false;
                continue;
            }

            if (!inBlock)
                result.Add(line);
        }

        return TrimExtraBlankLines(result);
    }

    static IReadOnlyList<string> RemoveAllKnownBlocks(IReadOnlyList<string> existing)
    {
        var knownStarts = PlatformProviders.All.Select(p => GetMarkerStart(p.Descriptor.Id))
            .Append(LegacyMarkStart)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownEnds = PlatformProviders.All.Select(p => GetMarkerEnd(p.Descriptor.Id))
            .Append(LegacyMarkEnd)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var inBlock = false;

        foreach (var line in existing)
        {
            var trimmed = line.Trim();
            if (knownStarts.Contains(trimmed))
            {
                inBlock = true;
                continue;
            }

            if (knownEnds.Contains(trimmed))
            {
                inBlock = false;
                continue;
            }

            if (!inBlock)
                result.Add(line);
        }

        return TrimExtraBlankLines(result);
    }

    static IReadOnlyList<string> TrimExtraBlankLines(List<string> lines)
    {
        while (lines.Count > 1 &&
            string.IsNullOrWhiteSpace(lines[^1]) &&
            string.IsNullOrWhiteSpace(lines[^2]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    static void ReplaceHostsAtomically(IReadOnlyList<string> lines)
    {
        var hostsDirectory = Path.GetDirectoryName(HostsPath)!;
        var tempPath = Path.Combine(hostsDirectory, $"hosts.githubturbo.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllLines(tempPath, lines, NoBomUtf8);
            if (File.Exists(HostsPath))
                File.Replace(tempPath, HostsPath, null, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, HostsPath);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch { }
        }
    }

    static string CreateBackupPath()
    {
        AppPaths.EnsureDirectories();
        return Path.Combine(AppPaths.BackupDirectory, $"hosts-{DateTime.Now:yyyyMMdd-HHmmss-fff}.bak");
    }

    static HostsOperationResult FlushDnsCache()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ipconfig", "/flushdns")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
                return HostsOperationResult.Fail("无法启动 ipconfig。");

            if (!process.WaitForExit(5000))
                return HostsOperationResult.Fail("ipconfig /flushdns 执行超时。");

            return process.ExitCode == 0
                ? HostsOperationResult.Ok("DNS 缓存已刷新。")
                : HostsOperationResult.Fail($"ipconfig /flushdns 退出码：{process.ExitCode}");
        }
        catch (Exception ex)
        {
            return HostsOperationResult.Fail(ex.Message);
        }
    }

}

public sealed record HostsOperationResult(bool Success, string Message, string? Path = null)
{
    public static HostsOperationResult Ok(string message, string? path = null) => new(true, message, path);

    public static HostsOperationResult Fail(string message, string? path = null) => new(false, message, path);
}

public sealed record HostsConflictEntry(string Domain, string Ip, int LineNumber, bool IsLoopbackOrPrivate);
