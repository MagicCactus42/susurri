using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Susurri.Modules.DHT.Core.Kademlia;
using Xunit;

namespace Susurri.Tests.Integration;

[Collection("DhtIntegration")]
public class UdpTransportTests
{
    private static KademliaDhtNode MakeUdpNode(out NodeKeyMaterial keys)
    {
        keys = NodeKeyMaterial.Create(false);
        return new KademliaDhtNode(
            keys.Encryption,
            NullLogger<KademliaDhtNode>.Instance,
            keys.Signing,
            natTraversal: null,
            enableUdpTransport: true,
            useStun: false);
    }

    [Fact]
    public async Task Two_Udp_Nodes_Bootstrap_Ping_And_Learn_Each_Other()
    {
        await using var a = MakeUdpNode(out _);
        await using var b = MakeUdpNode(out _);

        await a.StartAsync(0);
        await b.StartAsync(0);

        a.UdpEnabled.ShouldBeTrue();

        var bEp = new IPEndPoint(IPAddress.Loopback, b.LocalPort);
        var aEp = new IPEndPoint(IPAddress.Loopback, a.LocalPort);

        // Ping travels over the UDP transport (UDP-first), the Pong returns over
        // UDP and completes the pending request.
        (await a.PingEndpointAsync(bEp)).ShouldBeTrue();

        await a.BootstrapAsync(new[] { bEp });
        await b.BootstrapAsync(new[] { aEp });

        a.KnownNodes.ShouldBeGreaterThanOrEqualTo(1);
        b.KnownNodes.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Store_And_FindValue_Round_Trip_Over_Udp()
    {
        await using var a = MakeUdpNode(out _);
        await using var b = MakeUdpNode(out _);

        await a.StartAsync(0);
        await b.StartAsync(0);

        await a.BootstrapAsync(new[] { new IPEndPoint(IPAddress.Loopback, b.LocalPort) });
        await b.BootstrapAsync(new[] { new IPEndPoint(IPAddress.Loopback, a.LocalPort) });

        var key = KademliaId.FromString("udp-integration-key");
        var value = Encoding.UTF8.GetBytes("value carried entirely over the reliable UDP transport");

        await a.StoreValueAsync(key, value);

        var found = await b.FindValueAsync(key);
        found.ShouldNotBeNull();
        found.ShouldBe(value);
    }

    [Fact]
    public async Task Large_Value_Fragments_Across_Udp_And_Reassembles()
    {
        await using var a = MakeUdpNode(out _);
        await using var b = MakeUdpNode(out _);

        await a.StartAsync(0);
        await b.StartAsync(0);

        await a.BootstrapAsync(new[] { new IPEndPoint(IPAddress.Loopback, b.LocalPort) });
        await b.BootstrapAsync(new[] { new IPEndPoint(IPAddress.Loopback, a.LocalPort) });

        var key = KademliaId.FromString("udp-large-value");
        var value = new byte[20_000]; // ~20 UDP fragments
        new Random(23).NextBytes(value);

        await a.StoreValueAsync(key, value);

        var found = await b.FindValueAsync(key);
        found.ShouldNotBeNull();
        found.ShouldBe(value);
    }

    [Fact]
    public async Task Hole_Punch_Between_Two_Nodes_Succeeds()
    {
        await using var a = MakeUdpNode(out _);
        await using var b = MakeUdpNode(out _);

        await a.StartAsync(0);
        await b.StartAsync(0);

        var punchId = Guid.NewGuid();
        var aTask = a.HolePunchAsync(punchId, new IPEndPoint(IPAddress.Loopback, b.LocalPort));
        var bTask = b.HolePunchAsync(punchId, new IPEndPoint(IPAddress.Loopback, a.LocalPort));

        var results = await Task.WhenAll(aTask, bTask).WaitAsync(TimeSpan.FromSeconds(12));
        results.ShouldAllBe(r => r);
    }
}
