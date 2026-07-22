using System.IO;
using System.Text;

namespace GitHubTurbo;

public static class HostsFileService
{
    const string HostsPath = @"C:\Windows\System32\drivers\etc\hosts";
    const long MaxFileLength = 52428800;

    internal const string LegacyMarkStart = "# GitHubTurbo Start";
    internal const string LegacyMarkEnd = "# GitHubTurbo End";

    public static string GetMarkerStart(string platformId) => $"# {platformId} Start";
    public static string GetMarkerEnd(string platformId) => $"# {platformId} End";

    public static bool HasAnyEntries =>
        File.Exists(HostsPath) && File.ReadAllText(HostsPath).Contains(LegacyMarkEnd);

    public static bool HasEntries(string platformId)
    {
        if (!File.Exists(HostsPath))
            return false;
        var content = File.ReadAllText(HostsPath);
        return content.Contains(GetMarkerStart(platformId)) || content.Contains(GetMarkerEnd(platformId));
    }

    public static bool HasLegacyEntries =>
        File.Exists(HostsPath) && File.ReadAllText(HostsPath).Contains(LegacyMarkEnd);

    public static void EnsureDefaultHosts()
    {
        var fileInfo = new FileInfo(HostsPath);
        if (fileInfo.Exists)
            return;

        using var stream = fileInfo.Create();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
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
        writer.WriteLine("# For example:");
        writer.WriteLine("#");
        writer.WriteLine("#      102.168.1.2     rhino.acme.com          # source server");
        writer.WriteLine("#       38.25.63.10     x.acme.com              # x client host");
        writer.WriteLine();
        writer.WriteLine("# localhost name resolution is handled within DNS itself.");
        writer.WriteLine("#	127.0.0.1       localhost");
        writer.WriteLine("#	::1             localhost");
    }

    public static bool ApplyHosts(string platformId, IReadOnlyDictionary<string, string> domainToIp)
    {
        var fileInfo = new FileInfo(HostsPath);

        if (!fileInfo.Exists)
            EnsureDefaultHosts();

        if (fileInfo.Length > MaxFileLength)
            return false;

        var wasReadOnly = fileInfo.IsReadOnly;
        if (wasReadOnly)
        {
            try { fileInfo.IsReadOnly = false; }
            catch { return false; }
        }

        try
        {
            var existing = File.ReadAllLines(HostsPath, Encoding.UTF8);
            var result = new List<string>();
            var markerStart = GetMarkerStart(platformId);
            var markerEnd = GetMarkerEnd(platformId);
            var inBlock = false;
            var inserted = false;

            foreach (var line in existing)
            {
                if (line.Trim().Equals(markerStart, StringComparison.OrdinalIgnoreCase))
                {
                    inBlock = true;
                    if (!inserted)
                    {
                        result.Add(markerStart);
                        foreach (var kv in domainToIp)
                            result.Add($"{kv.Value} {kv.Key}");
                        result.Add(markerEnd);
                        inserted = true;
                    }
                    continue;
                }

                if (line.Trim().Equals(markerEnd, StringComparison.OrdinalIgnoreCase))
                {
                    inBlock = false;
                    continue;
                }

                if (!inBlock)
                    result.Add(line);
            }

            if (!inserted)
            {
                if (result.Count > 0 && !string.IsNullOrWhiteSpace(result[^1]))
                    result.Add(string.Empty);
                result.Add(markerStart);
                foreach (var kv in domainToIp)
                    result.Add($"{kv.Value} {kv.Key}");
                result.Add(markerEnd);
            }

            File.WriteAllLines(HostsPath, result, Encoding.UTF8);
            FlushDnsCache();
            return true;
        }
        finally
        {
            if (wasReadOnly)
            {
                try { fileInfo.IsReadOnly = true; }
                catch { }
            }
        }
    }

    public static bool RemoveHosts(string platformId)
    {
        var fileInfo = new FileInfo(HostsPath);
        if (!fileInfo.Exists)
            return true;

        var wasReadOnly = fileInfo.IsReadOnly;
        if (wasReadOnly)
        {
            try { fileInfo.IsReadOnly = false; }
            catch { return false; }
        }

        try
        {
            var lines = File.ReadAllLines(HostsPath, Encoding.UTF8);
            var result = new List<string>();
            var markerStart = GetMarkerStart(platformId);
            var markerEnd = GetMarkerEnd(platformId);
            var inBlock = false;

            foreach (var line in lines)
            {
                if (line.Trim().Equals(markerStart, StringComparison.OrdinalIgnoreCase))
                {
                    inBlock = true;
                    continue;
                }

                if (line.Trim().Equals(markerEnd, StringComparison.OrdinalIgnoreCase))
                {
                    inBlock = false;
                    continue;
                }

                if (!inBlock)
                    result.Add(line);
            }

            File.WriteAllLines(HostsPath, result, Encoding.UTF8);
            FlushDnsCache();
            return true;
        }
        finally
        {
            if (wasReadOnly)
            {
                try { fileInfo.IsReadOnly = true; }
                catch { }
            }
        }
    }

    public static bool RemoveAllHosts()
    {
        var fileInfo = new FileInfo(HostsPath);
        if (!fileInfo.Exists)
            return true;

        var wasReadOnly = fileInfo.IsReadOnly;
        if (wasReadOnly)
        {
            try { fileInfo.IsReadOnly = false; }
            catch { return false; }
        }

        try
        {
            var lines = File.ReadAllLines(HostsPath, Encoding.UTF8);
            var result = new List<string>();
            var inAnyBlock = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("# ", StringComparison.Ordinal) && trimmed.EndsWith(" Start", StringComparison.Ordinal))
                {
                    inAnyBlock = true;
                    continue;
                }

                if (trimmed.StartsWith("# ", StringComparison.Ordinal) && trimmed.EndsWith(" End", StringComparison.Ordinal))
                {
                    inAnyBlock = false;
                    continue;
                }

                if (trimmed.Equals(LegacyMarkStart, StringComparison.OrdinalIgnoreCase))
                {
                    inAnyBlock = true;
                    continue;
                }

                if (trimmed.Equals(LegacyMarkEnd, StringComparison.OrdinalIgnoreCase))
                {
                    inAnyBlock = false;
                    continue;
                }

                if (!inAnyBlock)
                    result.Add(line);
            }

            File.WriteAllLines(HostsPath, result, Encoding.UTF8);
            FlushDnsCache();
            return true;
        }
        finally
        {
            if (wasReadOnly)
            {
                try { fileInfo.IsReadOnly = true; }
                catch { }
            }
        }
    }

    static void FlushDnsCache()
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
            process?.WaitForExit(5000);
        }
        catch { }
    }
}
