using System.Net;
using System.Net.Sockets;

namespace GitHubTurbo;

public static class IpAddressHelpers
{
    public static bool IsPrivateOrLoopback(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
                return IsPrivateOrLoopback(address.MapToIPv4());

            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
                return true;

            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
                return true;

            // fc00::/7 unique local addresses
            return (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var bytes = address.GetAddressBytes();
        return bytes[0] == 0 ||
            bytes[0] == 10 ||
            (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168) ||
            (bytes[0] == 169 && bytes[1] == 254) ||
            (bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2) ||
            (bytes[0] == 198 && bytes[1] is 18 or 19) ||
            (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) ||
            (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) ||
            bytes[0] >= 224;
    }

    public static bool IsPrivateOrLoopback(string ip)
        => IPAddress.TryParse(ip, out var address) && IsPrivateOrLoopback(address);

    public static bool IsUsablePublicIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.None))
            return false;

        return !IsPrivateOrLoopback(address);
    }

    public static bool IsUsablePublicIpv6(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        if (address.IsIPv4MappedToIPv6)
            return IsUsablePublicIpv4(address.MapToIPv4());

        if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
            return false;

        if (IsPrivateOrLoopback(address))
            return false;

        // 2001:db8::/32 documentation prefix
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
            return false;

        return true;
    }
}
