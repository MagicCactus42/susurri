using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Susurri.Modules.DHT.Core.Kademlia;

namespace Susurri.Tests.Integration;

[Collection("DhtIntegration")]
public class OfflineMessageRecoveryTests
{
    [Fact]
    public async Task Offline_Message_Is_Recovered_After_Recipient_Restart()
    {
        // 4-node cluster. Bob is exportable so we can rebuild the same identity
        // after his "restart". The other nodes will hold his offline messages
        // while he's down.
        await using var cluster = await DhtCluster.StartAsync(count: 4, exportableKeys: true);

        var alice = cluster.Nodes[0];
        var bobInitial = cluster.Nodes[3]; // last-indexed node
        var bobKeys = cluster.Keys[3];
        var bobPublicKey = bobKeys.EncryptionPublicKey;

        // Bob shuts down. The other three nodes still have him in their routing
        // tables; Alice's StoreOfflineMessageAsync will replicate to nodes
        // closest to Bob's KademliaId — which include the still-running peers.
        await bobInitial.DisposeAsync();

        var encryptedPayload = new byte[] { 0x42, 0x4f, 0x42 }; // "BOB"
        await alice.StoreOfflineMessageAsync(bobPublicKey, encryptedPayload);

        // Bob comes back online. Same identity (KademliaId derived from
        // public key), fresh process. He bootstraps against one of the
        // peers that held the offline message.
        var (bobEnc2, bobSign2) = bobKeys.ReimportKeys();
        await using var bobRevived = new KademliaDhtNode(
            bobEnc2, NullLogger<KademliaDhtNode>.Instance, bobSign2);
        await bobRevived.StartAsync(0);

        var seedEndpoint = new IPEndPoint(
            IPAddress.Loopback,
            cluster.Nodes[0].LocalEndPoint!.Port);
        await bobRevived.BootstrapAsync(new[] { seedEndpoint });

        // Bob's KademliaId must be stable across restart — that's what makes
        // offline-message lookup work in the first place.
        bobRevived.LocalId.ShouldBe(bobInitial.LocalId);

        var messages = await bobRevived.GetOfflineMessagesAsync();

        messages.Any(m => m.SequenceEqual(encryptedPayload))
            .ShouldBeTrue("Bob should recover the offline message Alice sent while he was down");
    }

    [Fact]
    public async Task GetOfflineMessages_Returns_Empty_When_None_Stored()
    {
        await using var cluster = await DhtCluster.StartAsync(count: 3);

        var messages = await cluster.Nodes[0].GetOfflineMessagesAsync();

        messages.ShouldBeEmpty();
    }
}
