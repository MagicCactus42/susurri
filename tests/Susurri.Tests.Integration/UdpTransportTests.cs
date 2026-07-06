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

    [Fact]
    public async Task Nodes_On_Different_Networks_Do_Not_Connect()
    {
        var keysA = NodeKeyMaterial.Create(false);
        var keysB = NodeKeyMaterial.Create(false);

        await using var a = new KademliaDhtNode(keysA.Encryption, NullLogger<KademliaDhtNode>.Instance,
            keysA.Signing, enableUdpTransport: true, networkId: 0x11111111);
        await using var b = new KademliaDhtNode(keysB.Encryption, NullLogger<KademliaDhtNode>.Instance,
            keysB.Signing, enableUdpTransport: true, networkId: 0x22222222);

        await a.StartAsync(0);
        await b.StartAsync(0);

        // A tries to join B, but B belongs to a different network and drops A's
        // messages, so neither learns the other.
        await a.BootstrapAsync(new[] { new IPEndPoint(IPAddress.Loopback, b.LocalPort) });

        a.KnownNodes.ShouldBe(0);
        b.KnownNodes.ShouldBe(0);
    }

    [Fact]
    public async Task Nodes_On_Same_Network_Connect()
    {
        var keysA = NodeKeyMaterial.Create(false);
        var keysB = NodeKeyMaterial.Create(false);

        await using var a = new KademliaDhtNode(keysA.Encryption, NullLogger<KademliaDhtNode>.Instance,
            keysA.Signing, enableUdpTransport: true, networkId: 0x33333333);
        await using var b = new KademliaDhtNode(keysB.Encryption, NullLogger<KademliaDhtNode>.Instance,
            keysB.Signing, enableUdpTransport: true, networkId: 0x33333333);

        await a.StartAsync(0);
        await b.StartAsync(0);

        await a.BootstrapAsync(new[] { new IPEndPoint(IPAddress.Loopback, b.LocalPort) });

        a.KnownNodes.ShouldBeGreaterThanOrEqualTo(1);
        b.KnownNodes.ShouldBeGreaterThanOrEqualTo(1);
    }

    private static void SeedPublicEndpoint(KademliaDhtNode node) =>
        node.SetPublicUdpEndpoint(new IPEndPoint(IPAddress.Loopback, node.LocalPort));

    private static KademliaNode NodeRef(KademliaDhtNode node) =>
        new(node.LocalId, node.EncryptionPublicKey, new IPEndPoint(IPAddress.Loopback, node.LocalPort));

    [Fact]
    public async Task Coordinated_Punch_Through_Intermediary_Resolves_Target()
    {
        // Topology: A knows only the intermediary I; I knows the target B.
        // A must reach B by signalling a hole punch through I over the DHT.
        await using var a = MakeUdpNode(out _);
        await using var intermediary = MakeUdpNode(out _);
        await using var b = MakeUdpNode(out _);

        await a.StartAsync(0);
        await intermediary.StartAsync(0);
        await b.StartAsync(0);

        SeedPublicEndpoint(a);
        SeedPublicEndpoint(b);

        // The intermediary and B know each other; A only knows the intermediary.
        await intermediary.BootstrapAsync(new[] { new IPEndPoint(IPAddress.Loopback, b.LocalPort) });
        await b.BootstrapAsync(new[] { new IPEndPoint(IPAddress.Loopback, intermediary.LocalPort) });

        var resolved = await a.HolePunchThroughAsync(NodeRef(intermediary), b.LocalId)
            .WaitAsync(TimeSpan.FromSeconds(15));

        resolved.ShouldNotBeNull();
        resolved.Port.ShouldBe(b.LocalPort);

        // Having punched, A can now talk to B directly at the resolved endpoint.
        (await a.PingEndpointAsync(resolved)).ShouldBeTrue();
    }

    [Fact]
    public async Task SendRequestToNode_Falls_Back_To_Coordinated_Punch_When_Direct_Fails()
    {
        await using var a = MakeUdpNode(out _);
        await using var intermediary = MakeUdpNode(out _);
        await using var b = MakeUdpNode(out _);

        await a.StartAsync(0);
        await intermediary.StartAsync(0);
        await b.StartAsync(0);

        SeedPublicEndpoint(a);
        SeedPublicEndpoint(b);

        await intermediary.BootstrapAsync(new[] { new IPEndPoint(IPAddress.Loopback, b.LocalPort) });
        await b.BootstrapAsync(new[] { new IPEndPoint(IPAddress.Loopback, intermediary.LocalPort) });
        await a.BootstrapAsync(new[] { new IPEndPoint(IPAddress.Loopback, intermediary.LocalPort) });

        // A holds a record for B with a dead direct endpoint (port nobody listens on).
        var unreachableB = new KademliaNode(
            b.LocalId, b.EncryptionPublicKey, new IPEndPoint(IPAddress.Loopback, 1));

        var request = new Susurri.Modules.DHT.Core.Kademlia.Protocol.FindNodeMessage
        {
            SenderId = a.LocalId,
            SenderPublicKey = a.EncryptionPublicKey,
            TargetId = a.LocalId
        };

        var response = await a.SendRequestToNodeAsync(unreachableB, request)
            .WaitAsync(TimeSpan.FromSeconds(20));

        response.ShouldNotBeNull();
        response.SenderId.ShouldBe(b.LocalId);
    }
}
