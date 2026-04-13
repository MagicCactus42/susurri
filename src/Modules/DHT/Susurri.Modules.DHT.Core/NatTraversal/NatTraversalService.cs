using System.Net;
using Microsoft.Extensions.Logging;

namespace Susurri.Modules.DHT.Core.NatTraversal;

/// <summary>
/// Orchestrates NAT traversal for the local node.
/// Manages STUN-based public endpoint discovery, NAT type detection,
/// and coordinates hole punching with remote peers.
/// </summary>
public sealed class NatTraversalService : IAsyncDisposable
{
    private readonly StunClient _stunClient;
    private readonly HolePunchService _holePunchService;
    private readonly ILogger<NatTraversalService> _logger;
    private readonly bool _useStun;
    private bool _disposed;

    private static readonly TimeSpan StunRefreshInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The node's public endpoint as discovered by STUN.
    /// Null if discovery hasn't run or failed.
    /// </summary>
    public IPEndPoint? PublicEndpoint { get; private set; }

    /// <summary>
    /// The detected NAT type.
    /// </summary>
    public NatType DetectedNatType { get; private set; } = NatType.Unknown;

    /// <summary>
    /// Whether hole punching is likely to succeed based on the detected NAT type.
    /// </summary>
    public bool CanHolePunch => DetectedNatType is NatType.ConeNat or NatType.OpenInternet;

    /// <summary>
    /// Whether the node appears to be directly reachable (no NAT).
    /// </summary>
    public bool IsPublic => DetectedNatType == NatType.OpenInternet;

    /// <summary>
    /// When the public endpoint was last refreshed.
    /// </summary>
    public DateTimeOffset LastRefresh { get; private set; }

    public NatTraversalService(
        StunClient stunClient,
        HolePunchService holePunchService,
        ILogger<NatTraversalService> logger,
        bool useStun = false)
    {
        _stunClient = stunClient;
        _holePunchService = holePunchService;
        _logger = logger;
        _useStun = useStun;
    }

    /// <summary>
    /// Discovers the public endpoint and NAT type. Should be called during node startup.
    /// No-op if STUN was not explicitly enabled at construction — STUN reveals the node's
    /// IP to third-party servers and is opt-in for privacy reasons.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!_useStun)
        {
            _logger.LogDebug("STUN disabled; skipping NAT traversal discovery");
            DetectedNatType = NatType.Unknown;
            LastRefresh = DateTimeOffset.UtcNow;
            return;
        }

        _logger.LogWarning(
            "STUN enabled — node IP will be revealed to third-party STUN servers. " +
            "This is a privacy tradeoff for NAT traversal. Disable via constructor flag if not needed.");

        var result = await _stunClient.DiscoverPublicEndpointAsync(ct: ct).ConfigureAwait(false);
        if (result != null)
        {
            PublicEndpoint = result.MappedEndPoint;
            _logger.LogInformation("Public endpoint discovered: {Endpoint}", PublicEndpoint);
        }
        else
        {
            _logger.LogWarning("Could not discover public endpoint via STUN");
        }

        DetectedNatType = await _stunClient.DetectNatTypeAsync(ct).ConfigureAwait(false);
        LastRefresh = DateTimeOffset.UtcNow;

        _logger.LogInformation("NAT type detected: {NatType}, hole punch capable: {CanPunch}",
            DetectedNatType, CanHolePunch);
    }

    /// <summary>
    /// Refreshes the public endpoint if it's stale.
    /// </summary>
    public async Task RefreshIfNeededAsync(CancellationToken ct = default)
    {
        if (!_useStun)
            return;

        if (DateTimeOffset.UtcNow - LastRefresh < StunRefreshInterval)
            return;

        var result = await _stunClient.DiscoverPublicEndpointAsync(ct: ct).ConfigureAwait(false);
        if (result != null)
        {
            var oldEndpoint = PublicEndpoint;
            PublicEndpoint = result.MappedEndPoint;
            LastRefresh = DateTimeOffset.UtcNow;

            if (oldEndpoint != null && !oldEndpoint.Equals(PublicEndpoint))
            {
                _logger.LogWarning("Public endpoint changed from {Old} to {New}",
                    oldEndpoint, PublicEndpoint);
            }
        }
    }

    /// <summary>
    /// Attempts a UDP hole punch to the given remote peer endpoint.
    /// Both peers must call this concurrently with the same punchId.
    /// </summary>
    /// <param name="punchId">Shared punch session ID.</param>
    /// <param name="remoteEndpoint">The peer's public UDP endpoint.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The punched UDP connection, or null on failure.</returns>
    public async Task<HolePunchResult?> HolePunchAsync(
        Guid punchId,
        IPEndPoint remoteEndpoint,
        CancellationToken ct = default)
    {
        if (!CanHolePunch)
        {
            _logger.LogDebug(
                "Skipping hole punch: local NAT type {NatType} does not support it",
                DetectedNatType);
            return null;
        }

        return await _holePunchService.PunchAsync(punchId, remoteEndpoint, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Formats the public endpoint as a string for inclusion in protocol messages.
    /// Returns empty string if no public endpoint is known.
    /// </summary>
    public string GetPublicEndpointString()
    {
        return PublicEndpoint != null
            ? $"{PublicEndpoint.Address}:{PublicEndpoint.Port}"
            : string.Empty;
    }

    /// <summary>
    /// Parses an endpoint string in "IP:port" format.
    /// </summary>
    public static IPEndPoint? ParseEndpoint(string endpointStr)
    {
        if (string.IsNullOrEmpty(endpointStr))
            return null;

        var lastColon = endpointStr.LastIndexOf(':');
        if (lastColon <= 0)
            return null;

        var ipStr = endpointStr[..lastColon];
        var portStr = endpointStr[(lastColon + 1)..];

        if (IPAddress.TryParse(ipStr, out var ip) && int.TryParse(portStr, out var port) &&
            port > 0 && port <= 65535)
        {
            return new IPEndPoint(ip, port);
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _holePunchService.DisposeAsync().ConfigureAwait(false);
    }
}
