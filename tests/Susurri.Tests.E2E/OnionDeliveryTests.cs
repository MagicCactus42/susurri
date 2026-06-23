using Shouldly;
using Susurri.Modules.DHT.Core.Onion;

namespace Susurri.Tests.E2E;

[Collection("OnionE2E")]
public class OnionDeliveryTests
{
    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task Ack_Returns_Through_Reply_Path_To_Sender()
    {
        await using var bed = await OnionTestbed.StartAsync(count: 5);

        // Bob's HandleChatDeliveryAsync auto-fires SendAckAsync after delivery.
        // The ACK travels back down the reply chain (r3 → r2 → r1 → alice) and
        // is unsealed at the sender-marker terminus — Alice — so it is Alice's
        // OnAckReceived that fires.
        var ackCaptured = new EventCapture<Guid>();
        bed.Alice.OnAckReceived += id => ackCaptured.HandleAsync(id);

        var message = TestChatMessage.CreateSigned(bed.AliceKeys, "ack-me");
        await bed.Alice.SendMessageAsync(message, bed.BobKeys.EncryptionPublicKey, bed.RelayPath());

        var ackedId = await ackCaptured.WaitFirstAsync(TimeSpan.FromSeconds(10));
        ackedId.ShouldBe(message.MessageId);
    }
}
