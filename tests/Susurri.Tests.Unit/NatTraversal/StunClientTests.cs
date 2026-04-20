using System.Buffers.Binary;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Susurri.Modules.DHT.Core.NatTraversal;
using Xunit;

namespace Susurri.Tests.Unit.NatTraversal;

public class StunClientTests
{
    private readonly StunClient _client = new(NullLogger<StunClient>.Instance);

    #region STUN Message Parsing Tests

    [Fact]
    public void DefaultStunServers_ContainsKnownServers()
    {
        Assert.True(StunClient.DefaultStunServers.Count >= 3);
        Assert.Contains(StunClient.DefaultStunServers,
            s => s.Host.Contains("google.com"));
        Assert.Contains(StunClient.DefaultStunServers,
            s => s.Host.Contains("cloudflare.com"));
    }

    [Fact]
    public void NatType_HasExpectedValues()
    {
        Assert.Equal(0, (byte)NatType.Unknown);
        Assert.Equal(1, (byte)NatType.OpenInternet);
        Assert.Equal(2, (byte)NatType.ConeNat);
        Assert.Equal(3, (byte)NatType.SymmetricNat);
        Assert.Equal(4, (byte)NatType.Blocked);
    }

    [Fact]
    public async Task BindingRequest_InvalidServer_ReturnsNull()
    {
        // A non-routable address should timeout and return null
        var badEndpoint = new IPEndPoint(IPAddress.Parse("192.0.2.1"), 3478);

        var result = await _client.BindingRequestAsync(badEndpoint);

        Assert.Null(result);
    }

    [Fact]
    public async Task BindingRequest_UnresolvableHost_ReturnsNull()
    {
        var badHost = new DnsEndPoint("stun.invalid.nonexistent.example", 3478);

        var result = await _client.BindingRequestAsync(badHost);

        Assert.Null(result);
    }

    #endregion

    #region STUN Binding Response Parsing (via reflection/synthetic)

    [Fact]
    public void StunBindingResult_StoresEndpointCorrectly()
    {
        var ep = new IPEndPoint(IPAddress.Parse("203.0.113.5"), 12345);
        var result = new StunBindingResult
        {
            MappedEndPoint = ep,
            LocalEndPoint = new IPEndPoint(IPAddress.Any, 5000)
        };

        Assert.Equal("203.0.113.5", result.MappedEndPoint.Address.ToString());
        Assert.Equal(12345, result.MappedEndPoint.Port);
        Assert.Equal(5000, result.LocalEndPoint!.Port);
        Assert.Null(result.OtherAddress);
    }

    #endregion
}
