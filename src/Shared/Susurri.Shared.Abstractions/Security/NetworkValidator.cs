using System.Net;
using System.Net.Sockets;

namespace Susurri.Shared.Abstractions.Security;

public static class NetworkValidator
{
    public static bool IsPubliclyRoutable(IPAddress? address)
    {
        if (address == null)
            return false;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return IsPubliclyRoutableV4(address);

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return IsPubliclyRoutableV6(address);

        return false;
    }

    private static bool IsPubliclyRoutableV4(IPAddress address)
    {
        var b = address.GetAddressBytes();

        if (b[0] == 0)
            return false;
        if (b[0] == 10)
            return false;
        if (b[0] == 100 && (b[1] & 0xC0) == 0x40)
            return false;
        if (b[0] == 127)
            return false;
        if (b[0] == 169 && b[1] == 254)
            return false;
        if (b[0] == 172 && (b[1] & 0xF0) == 0x10)
            return false;
        if (b[0] == 192 && b[1] == 0 && b[2] == 0)
            return false;
        if (b[0] == 192 && b[1] == 0 && b[2] == 2)
            return false;
        if (b[0] == 192 && b[1] == 168)
            return false;
        if (b[0] == 198 && (b[1] & 0xFE) == 0x12)
            return false;
        if (b[0] == 198 && b[1] == 51 && b[2] == 100)
            return false;
        if (b[0] == 203 && b[1] == 0 && b[2] == 113)
            return false;
        if (b[0] >= 224)
            return false;

        return true;
    }

    private static bool IsPubliclyRoutableV6(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return false;
        if (address.Equals(IPAddress.IPv6Any))
            return false;
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            return false;
        if (address.IsIPv6UniqueLocal)
            return false;
        if (address.IsIPv6Teredo)
            return false;

        return true;
    }
}
