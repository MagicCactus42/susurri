using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Susurri.Modules.DHT.Core.NatTraversal;

/// <summary>
/// Minimal STUN client implementing RFC 5389 Binding Request/Response
/// for discovering the node's public IP and port as seen by external servers.
/// </summary>
public sealed class StunClient
{
    private readonly ILogger<StunClient> _logger;

    private const uint MagicCookie = 0x2112A442;
    private const int HeaderSize = 20;
    private const ushort BindingRequest = 0x0001;
    private const ushort BindingResponse = 0x0101;

    // STUN attribute types
    private const ushort AttrMappedAddress = 0x0001;
    private const ushort AttrXorMappedAddress = 0x0020;
    private const ushort AttrOtherAddress = 0x802C;

    // Address families
    private const byte FamilyIPv4 = 0x01;
    private const byte FamilyIPv6 = 0x02;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private const int MaxRetries = 2;

    /// <summary>
    /// Well-known public STUN servers.
    /// </summary>
    public static readonly IReadOnlyList<DnsEndPoint> DefaultStunServers = new[]
    {
        new DnsEndPoint("stun.l.google.com", 19302),
        new DnsEndPoint("stun1.l.google.com", 19302),
        new DnsEndPoint("stun.cloudflare.com", 3478),
        new DnsEndPoint("stun.stunprotocol.org", 3478),
    };

    public StunClient(ILogger<StunClient> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Sends a STUN Binding Request to the given server and returns the mapped public endpoint.
    /// </summary>
    public async Task<StunBindingResult?> BindingRequestAsync(
        IPEndPoint serverEndPoint,
        UdpClient? existingClient = null,
        CancellationToken ct = default)
    {
        var transactionId = new byte[12];
        RandomNumberGenerator.Fill(transactionId);

        var request = BuildBindingRequest(transactionId);

        var ownsClient = existingClient == null;
        var client = existingClient ?? new UdpClient(0);

        try
        {
            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    await client.SendAsync(request, request.Length, serverEndPoint).ConfigureAwait(false);

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(RequestTimeout);

                    while (!cts.Token.IsCancellationRequested)
                    {
                        var result = await client.ReceiveAsync(cts.Token).ConfigureAwait(false);

                        var response = ParseBindingResponse(result.Buffer, transactionId);
                        if (response != null)
                        {
                            var localEp = (IPEndPoint)client.Client.LocalEndPoint!;
                            response.LocalEndPoint = localEp;
                            return response;
                        }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogDebug("STUN request to {Server} timed out (attempt {Attempt})",
                        serverEndPoint, attempt + 1);
                }
            }

            return null;
        }
        finally
        {
            if (ownsClient)
                client.Dispose();
        }
    }

    /// <summary>
    /// Resolves a STUN server hostname and performs a binding request.
    /// </summary>
    public async Task<StunBindingResult?> BindingRequestAsync(
        DnsEndPoint server,
        UdpClient? existingClient = null,
        CancellationToken ct = default)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(server.Host, ct).ConfigureAwait(false);
            var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

            if (ipv4 == null)
            {
                _logger.LogWarning("Could not resolve STUN server {Host} to IPv4", server.Host);
                return null;
            }

            return await BindingRequestAsync(new IPEndPoint(ipv4, server.Port), existingClient, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve STUN server {Host}", server.Host);
            return null;
        }
    }

    /// <summary>
    /// Discovers the public endpoint by trying multiple STUN servers.
    /// Returns the first successful result.
    /// </summary>
    public async Task<StunBindingResult?> DiscoverPublicEndpointAsync(
        IEnumerable<DnsEndPoint>? servers = null,
        CancellationToken ct = default)
    {
        servers ??= DefaultStunServers;

        foreach (var server in servers)
        {
            var result = await BindingRequestAsync(server, ct: ct).ConfigureAwait(false);
            if (result != null)
            {
                _logger.LogInformation("STUN discovery via {Server}: public endpoint is {Endpoint}",
                    server.Host, result.MappedEndPoint);
                return result;
            }
        }

        _logger.LogWarning("Failed to discover public endpoint from any STUN server");
        return null;
    }

    /// <summary>
    /// Detects the NAT type by performing multiple STUN queries from the same local port
    /// to different servers. Compares the mapped addresses to determine behavior.
    /// </summary>
    public async Task<NatType> DetectNatTypeAsync(CancellationToken ct = default)
    {
        using var client = new UdpClient(0);
        var localEp = (IPEndPoint)client.Client.LocalEndPoint!;

        var servers = DefaultStunServers.Take(3).ToList();
        if (servers.Count < 2)
            return NatType.Unknown;

        // Resolve servers
        var resolvedServers = new List<IPEndPoint>();
        foreach (var server in servers)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(server.Host, ct).ConfigureAwait(false);
                var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 != null)
                    resolvedServers.Add(new IPEndPoint(ipv4, server.Port));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to resolve STUN server {Host}", server.Host);
            }
        }

        if (resolvedServers.Count < 2)
            return NatType.Unknown;

        // Test 1: Query first server
        var result1 = await BindingRequestAsync(resolvedServers[0], client, ct).ConfigureAwait(false);
        if (result1 == null)
            return NatType.Blocked;

        // Check if we have a public IP (mapped == local)
        if (result1.MappedEndPoint.Address.Equals(localEp.Address))
            return NatType.OpenInternet;

        // Test 2: Query second server from same local port
        var result2 = await BindingRequestAsync(resolvedServers[1], client, ct).ConfigureAwait(false);
        if (result2 == null)
            return NatType.Unknown;

        // Compare mapped addresses from two different servers
        if (result1.MappedEndPoint.Address.Equals(result2.MappedEndPoint.Address) &&
            result1.MappedEndPoint.Port == result2.MappedEndPoint.Port)
        {
            // Same mapping for different destinations = Cone NAT (endpoint-independent mapping)
            // Could be Full Cone, Restricted, or Port Restricted - but all support hole punching
            return NatType.ConeNat;
        }

        // Different mapping per destination = Symmetric NAT (endpoint-dependent mapping)
        _logger.LogInformation(
            "Symmetric NAT detected: server1 mapped to {Ep1}, server2 mapped to {Ep2}",
            result1.MappedEndPoint, result2.MappedEndPoint);

        return NatType.SymmetricNat;
    }

    private static byte[] BuildBindingRequest(byte[] transactionId)
    {
        var packet = new byte[HeaderSize];

        // Message Type: Binding Request (0x0001)
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0), BindingRequest);
        // Message Length: 0 (no attributes)
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0);
        // Magic Cookie
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), MagicCookie);
        // Transaction ID (12 bytes)
        Array.Copy(transactionId, 0, packet, 8, 12);

        return packet;
    }

    private StunBindingResult? ParseBindingResponse(byte[] data, byte[] expectedTransactionId)
    {
        if (data.Length < HeaderSize)
            return null;

        var msgType = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0));
        if (msgType != BindingResponse)
            return null;

        var msgLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
        var cookie = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));

        if (cookie != MagicCookie)
            return null;

        // Verify transaction ID
        if (!data.AsSpan(8, 12).SequenceEqual(expectedTransactionId))
            return null;

        if (data.Length < HeaderSize + msgLength)
            return null;

        IPEndPoint? mappedEndPoint = null;
        IPEndPoint? otherAddress = null;

        // Parse attributes
        int offset = HeaderSize;
        int end = HeaderSize + msgLength;

        while (offset + 4 <= end)
        {
            var attrType = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
            var attrLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 2));
            offset += 4;

            if (offset + attrLength > end)
                break;

            switch (attrType)
            {
                case AttrXorMappedAddress:
                    mappedEndPoint = ParseXorMappedAddress(data.AsSpan(offset, attrLength), expectedTransactionId);
                    break;

                case AttrMappedAddress when mappedEndPoint == null:
                    mappedEndPoint = ParseMappedAddress(data.AsSpan(offset, attrLength));
                    break;

                case AttrOtherAddress:
                    otherAddress = ParseMappedAddress(data.AsSpan(offset, attrLength));
                    break;
            }

            // Attributes are padded to 4-byte boundaries
            offset += attrLength;
            offset = (offset + 3) & ~3;
        }

        if (mappedEndPoint == null)
        {
            _logger.LogWarning("STUN response contained no mapped address");
            return null;
        }

        return new StunBindingResult
        {
            MappedEndPoint = mappedEndPoint,
            OtherAddress = otherAddress
        };
    }

    private static IPEndPoint? ParseXorMappedAddress(ReadOnlySpan<byte> data, byte[] transactionId)
    {
        if (data.Length < 4)
            return null;

        var family = data[1];
        var xPort = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        var port = xPort ^ (ushort)(MagicCookie >> 16);

        if (family == FamilyIPv4 && data.Length >= 8)
        {
            var xAddr = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
            var addr = xAddr ^ MagicCookie;
            var addrBytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(addrBytes, addr);
            return new IPEndPoint(new IPAddress(addrBytes), port);
        }

        if (family == FamilyIPv6 && data.Length >= 20)
        {
            var addrBytes = new byte[16];
            var magicBytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(magicBytes, MagicCookie);

            for (int i = 0; i < 4; i++)
                addrBytes[i] = (byte)(data[4 + i] ^ magicBytes[i]);
            for (int i = 4; i < 16; i++)
                addrBytes[i] = (byte)(data[4 + i] ^ transactionId[i - 4]);

            return new IPEndPoint(new IPAddress(addrBytes), port);
        }

        return null;
    }

    private static IPEndPoint? ParseMappedAddress(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return null;

        var family = data[1];
        var port = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);

        if (family == FamilyIPv4 && data.Length >= 8)
        {
            var addrBytes = data[4..8].ToArray();
            return new IPEndPoint(new IPAddress(addrBytes), port);
        }

        if (family == FamilyIPv6 && data.Length >= 20)
        {
            var addrBytes = data[4..20].ToArray();
            return new IPEndPoint(new IPAddress(addrBytes), port);
        }

        return null;
    }
}

/// <summary>
/// Result from a STUN Binding Request.
/// </summary>
public sealed class StunBindingResult
{
    /// <summary>
    /// The public IP:port as seen by the STUN server.
    /// </summary>
    public IPEndPoint MappedEndPoint { get; init; } = null!;

    /// <summary>
    /// The local endpoint used for the request.
    /// </summary>
    public IPEndPoint? LocalEndPoint { get; set; }

    /// <summary>
    /// Alternate server address (if the STUN server supports RFC 5780).
    /// </summary>
    public IPEndPoint? OtherAddress { get; init; }
}

/// <summary>
/// Detected NAT type based on STUN probing.
/// </summary>
public enum NatType : byte
{
    /// <summary>Detection failed or insufficient data.</summary>
    Unknown = 0,

    /// <summary>No NAT — host has a public IP.</summary>
    OpenInternet = 1,

    /// <summary>NAT is present but all STUN servers see the same mapped endpoint.
    /// Hole punching will work (Full Cone, Restricted, or Port-Restricted).</summary>
    ConeNat = 2,

    /// <summary>Different servers see different mapped endpoints. Hole punching
    /// will not reliably work; must use relay.</summary>
    SymmetricNat = 3,

    /// <summary>UDP is completely blocked.</summary>
    Blocked = 4
}
