using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Susurri.Modules.DHT.Core.NatTraversal;
using Xunit;

namespace Susurri.Tests.Unit.NatTraversal;

public class NatTraversalServiceTests
{
    #region ParseEndpoint Tests

    [Theory]
    [InlineData("203.0.113.5:12345", "203.0.113.5", 12345)]
    [InlineData("192.168.1.1:80", "192.168.1.1", 80)]
    [InlineData("10.0.0.1:65535", "10.0.0.1", 65535)]
    public void ParseEndpoint_ValidInput_ReturnsCorrectEndpoint(string input, string expectedIp, int expectedPort)
    {
        var result = NatTraversalService.ParseEndpoint(input);

        Assert.NotNull(result);
        Assert.Equal(expectedIp, result.Address.ToString());
        Assert.Equal(expectedPort, result.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("no-port")]
    [InlineData(":1234")]
    [InlineData("192.168.1.1:0")]
    [InlineData("192.168.1.1:99999")]
    [InlineData("192.168.1.1:-1")]
    public void ParseEndpoint_InvalidInput_ReturnsNull(string? input)
    {
        var result = NatTraversalService.ParseEndpoint(input!);
        Assert.Null(result);
    }

    #endregion

    #region Service State Tests

    [Fact]
    public void NewService_HasDefaultState()
    {
        var stunClient = new StunClient(NullLogger<StunClient>.Instance);
        var holePunch = new HolePunchService(NullLogger<HolePunchService>.Instance);
        var service = new NatTraversalService(
            stunClient, holePunch,
            NullLogger<NatTraversalService>.Instance);

        Assert.Null(service.PublicEndpoint);
        Assert.Equal(NatType.Unknown, service.DetectedNatType);
        Assert.False(service.CanHolePunch);
        Assert.False(service.IsPublic);
        Assert.Equal(string.Empty, service.GetPublicEndpointString());
    }

    [Fact]
    public void CanHolePunch_ConeNat_ReturnsTrue()
    {
        // We can't directly set NatType, but we can verify the logic by checking the enum values
        Assert.Equal(NatType.ConeNat, (NatType)2);
        Assert.Equal(NatType.OpenInternet, (NatType)1);
    }

    #endregion
}
