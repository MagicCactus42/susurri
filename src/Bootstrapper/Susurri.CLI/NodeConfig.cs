using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Susurri.Modules.DHT.Core.Kademlia.Protocol;
using Susurri.Modules.DHT.Core.Services;

namespace Susurri.CLI;

/// <summary>
/// Reads the shared DHT/NAT/network settings from configuration so the
/// <c>login</c> chat node and the headless <c>dht start</c> node behave
/// identically.
/// </summary>
internal static class NodeConfig
{
    public static uint NetworkId(IConfiguration config) => ParseNetworkId(config["DHT:NetworkId"]);

    public static ChatNodeOptions ChatOptions(IConfiguration config) => new(
        EnableUdp: config.GetValue("DHT:Nat:Enabled", true),
        UseStun: config.GetValue("DHT:Nat:UseStun", false),
        NetworkId: NetworkId(config),
        PublicEndpoint: ParseEndpoint(config["DHT:Nat:PublicEndpoint"]),
        AllowLoopback: config.GetValue("DHT:AllowLoopback", false));

    public static List<IPEndPoint> Seeds(IConfiguration config, IEnumerable<string> extra)
    {
        var configured = config.GetSection("DHT:BootstrapNodes").Get<string[]>() ?? Array.Empty<string>();
        return extra
            .Concat(configured)
            .Select(ParseEndpoint)
            .Where(e => e != null)
            .Select(e => e!)
            .GroupBy(e => e.ToString())
            .Select(g => g.First())
            .ToList();
    }

    public static IPEndPoint? ParseEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        var text = endpoint.Trim();
        var lastColon = text.LastIndexOf(':');
        if (lastColon <= 0)
            return null;

        var host = text[..lastColon];
        var portText = text[(lastColon + 1)..];
        if (IPAddress.TryParse(host, out var ip) &&
            int.TryParse(portText, out var port) && port > 0 && port <= 65535)
        {
            return new IPEndPoint(ip, port);
        }

        return null;
    }

    public static uint ParseNetworkId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return KademliaMessage.DefaultNetworkId;

        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, null, out var hex))
            return hex;

        if (uint.TryParse(text, out var dec))
            return dec;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return BinaryPrimitives.ReadUInt32BigEndian(hash);
    }
}
