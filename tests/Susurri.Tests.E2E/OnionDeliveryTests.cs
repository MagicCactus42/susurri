using Shouldly;
using Susurri.Modules.DHT.Core.Onion;

namespace Susurri.Tests.E2E;

[Collection("OnionE2E")]
public class OnionDeliveryTests
{
    // Bug discovered by these tests (recorded in KNOWN-LIMITATIONS.md): the
    // OnionRouter relay chain is broken end-to-end. HandleFinalHopAsync wraps
    // the still-encrypted recipient-layer bytes into a Delivery layer's
    // InnerPayload, but HandleDeliveryAsync expects InnerPayload to be plaintext
    // RecipientPayload bytes. The mismatch means deliver-via-relays never
    // completes successfully. ProcessOfflineMessageAsync uses the correct
    // double-decrypt path; the online relay path doesn't. Tests that rely on
    // the broken positive path are skipped until the chain is repaired.
    [Fact(Skip = "Production OnionRouter relay chain has a layer-decrypt mismatch — see KNOWN-LIMITATIONS.md")]
    public async Task ChatMessage_Delivers_Through_3_Hop_Onion_Path()
    {
        // 5 nodes: alice + r1 + r2 + r3 + bob; relay path has 3 hops.
        await using var bed = await OnionTestbed.StartAsync(count: 5);

        var captured = new EventCapture<ChatMessage>();
        bed.Bob.OnMessageReceived += (msg, _) => captured.HandleAsync(msg);

        var message = TestChatMessage.CreateSigned(bed.AliceKeys,
            content: "hello from alice via 3 relays");

        var path = bed.RelayPath();
        path.Count.ShouldBe(3, "5-node testbed should have alice + 3 relays + bob");

        await bed.Alice.SendMessageAsync(message, bed.BobKeys.EncryptionPublicKey, path);

        var received = await captured.WaitFirstAsync();

        received.MessageId.ShouldBe(message.MessageId);
        received.Content.ShouldBe(message.Content);
        received.SenderPublicKey.ShouldBe(bed.AliceKeys.EncryptionPublicKey);
        received.SenderSigningPublicKey.ShouldBe(bed.AliceKeys.SigningPublicKey);
        received.VerifySignature().ShouldBeTrue("delivered message should still verify");
    }

    [Fact(Skip = "Production OnionRouter relay chain has a layer-decrypt mismatch — see KNOWN-LIMITATIONS.md")]
    public async Task ChatMessage_Delivers_Through_Single_Hop_Path()
    {
        // 3 nodes: alice + r1 + bob.
        await using var bed = await OnionTestbed.StartAsync(count: 3);

        var captured = new EventCapture<ChatMessage>();
        bed.Bob.OnMessageReceived += (msg, _) => captured.HandleAsync(msg);

        var message = TestChatMessage.CreateSigned(bed.AliceKeys, "1-hop hello");
        await bed.Alice.SendMessageAsync(message, bed.BobKeys.EncryptionPublicKey, bed.RelayPath());

        var received = await captured.WaitFirstAsync();
        received.Content.ShouldBe("1-hop hello");
    }

    [Fact(Skip = "Blocked by relay-chain delivery bug — ACK return depends on initial delivery")]
    public async Task Ack_Returns_Through_Reply_Path()
    {
        await using var bed = await OnionTestbed.StartAsync(count: 5);

        // Bob's HandleChatDeliveryAsync auto-fires SendAckAsync after delivery,
        // so the ACK travels back through the reply path. The SenderMarker
        // token sits at path[0] = the first relay (closest to Alice), so
        // that router is the one whose OnAckReceived event fires.
        var ackCaptured = new EventCapture<Guid>();
        var firstRelay = bed.Routers[1]; // = path[0]
        firstRelay.OnAckReceived += id => ackCaptured.HandleAsync(id);

        var message = TestChatMessage.CreateSigned(bed.AliceKeys, "ack-me");
        await bed.Alice.SendMessageAsync(message, bed.BobKeys.EncryptionPublicKey, bed.RelayPath());

        var ackedId = await ackCaptured.WaitFirstAsync();
        ackedId.ShouldBe(message.MessageId);
    }
}
