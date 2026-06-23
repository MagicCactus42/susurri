using NSec.Cryptography;
using Shouldly;
using Susurri.Modules.DHT.Core.Onion;

namespace Susurri.Tests.E2E;

[Collection("OnionE2E")]
public class OnionRejectionTests
{
    [Fact]
    public async Task Tampered_Signature_Causes_Bob_To_Drop_Message()
    {
        await using var bed = await OnionTestbed.StartAsync(count: 5);

        var captured = new EventCapture<ChatMessage>();
        bed.Bob.OnMessageReceived += (msg, _) => captured.HandleAsync(msg);

        // Sign the message normally, then tamper with its content after signing.
        // The signature is now invalid; Bob's HandleChatDeliveryAsync should drop it.
        var msg = TestChatMessage.CreateSigned(bed.AliceKeys, "original");
        var tampered = new ChatMessage
        {
            MessageId = msg.MessageId,
            SenderPublicKey = msg.SenderPublicKey,
            SenderSigningPublicKey = msg.SenderSigningPublicKey,
            Content = "TAMPERED",
            Timestamp = msg.Timestamp,
            Signature = msg.Signature
        };
        tampered.VerifySignature().ShouldBeFalse(
            "the tampered message should be invalid before we even send it");

        await bed.Alice.SendMessageAsync(tampered, bed.BobKeys.EncryptionPublicKey, bed.RelayPath());

        // Give it time to propagate; assert the event never fired.
        await Task.Delay(500);
        captured.All.IsEmpty.ShouldBeTrue("tampered message must not be delivered");
    }

    [Fact]
    public async Task Replayed_Message_Is_Dropped_On_Second_Arrival()
    {
        await using var bed = await OnionTestbed.StartAsync(count: 5);

        var captured = new EventCapture<ChatMessage>();
        bed.Bob.OnMessageReceived += (msg, _) => captured.HandleAsync(msg);

        var msg = TestChatMessage.CreateSigned(bed.AliceKeys, "deduped");

        await bed.Alice.SendMessageAsync(msg, bed.BobKeys.EncryptionPublicKey, bed.RelayPath());
        await captured.WaitFirstAsync();

        captured.All.Count.ShouldBe(1, "first send arrives normally");

        // Send the SAME ChatMessage (same MessageId) a second time. Phase 1's
        // replay cache on Bob's OnionRouter should drop it.
        await bed.Alice.SendMessageAsync(msg, bed.BobKeys.EncryptionPublicKey, bed.RelayPath());
        await Task.Delay(500);

        captured.All.Count.ShouldBe(1, "the replayed message should be silently dropped");
    }

    [Fact]
    public async Task Stale_Timestamp_Is_Rejected_By_Recipient()
    {
        await using var bed = await OnionTestbed.StartAsync(count: 5);

        var captured = new EventCapture<ChatMessage>();
        bed.Bob.OnMessageReceived += (msg, _) => captured.HandleAsync(msg);

        // Build a message with a timestamp 10 minutes in the past — outside
        // Phase 1's ±5-minute freshness window.
        var stale = new ChatMessage
        {
            SenderPublicKey = bed.AliceKeys.EncryptionPublicKey,
            SenderSigningPublicKey = bed.AliceKeys.SigningPublicKey,
            Content = "old news",
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds()
        };
        stale.Signature = SignatureAlgorithm.Ed25519.Sign(bed.AliceKeys.Signing, stale.GetSignableData());

        // Sanity check — signature is valid, so freshness is the only thing
        // that should reject the message.
        stale.VerifySignature().ShouldBeTrue();

        await bed.Alice.SendMessageAsync(stale, bed.BobKeys.EncryptionPublicKey, bed.RelayPath());

        await Task.Delay(500);
        captured.All.IsEmpty.ShouldBeTrue("stale-timestamp message must not be delivered");
    }
}
