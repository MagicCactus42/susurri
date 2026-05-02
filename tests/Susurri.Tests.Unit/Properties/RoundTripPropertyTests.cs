using CsCheck;
using Shouldly;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Kademlia.Protocol;
using Susurri.Modules.DHT.Core.Onion;
using Xunit;

namespace Susurri.Tests.Unit.Properties;

/// <summary>
/// Property: for a freshly-built valid instance of every protocol message type,
/// Deserialize(Serialize(x)) ≡ x. Catches serializer bugs that drift between
/// production and test code. CsCheck runs each property with 100 random inputs
/// (plus shrinking on failure).
/// </summary>
public class RoundTripPropertyTests
{
    [Fact]
    public void ChatMessage_RoundTrips()
    {
        MsgGen.ChatMessageGen.Sample(msg =>
        {
            var bytes = msg.Serialize();
            var deser = ChatMessage.Deserialize(bytes);

            deser.SenderPublicKey.ShouldBe(msg.SenderPublicKey);
            deser.SenderSigningPublicKey.ShouldBe(msg.SenderSigningPublicKey);
            deser.Content.ShouldBe(msg.Content);
            deser.Timestamp.ShouldBe(msg.Timestamp);
            deser.MessageId.ShouldBe(msg.MessageId);
            deser.Signature.ShouldBe(msg.Signature);
        });
    }

    [Fact]
    public void UserPublicKeyRecord_Signed_RoundTrips()
    {
        MsgGen.UserPublicKeyRecordGen.Sample(record =>
        {
            var bytes = record.Serialize();
            var deser = UserPublicKeyRecord.Deserialize(bytes);

            deser.EncryptionPublicKey.ShouldBe(record.EncryptionPublicKey);
            deser.SigningPublicKey.ShouldBe(record.SigningPublicKey);
            deser.Timestamp.ShouldBe(record.Timestamp);
            deser.Signature.ShouldBe(record.Signature);
        });
    }

    [Fact]
    public void UserPublicKeyRecord_Unsigned_RoundTrips()
    {
        MsgGen.UserPublicKeyRecordUnsignedGen.Sample(record =>
        {
            var bytes = record.Serialize();
            var deser = UserPublicKeyRecord.Deserialize(bytes);

            deser.EncryptionPublicKey.ShouldBe(record.EncryptionPublicKey);
            deser.SigningPublicKey.ShouldBe(record.SigningPublicKey);
            deser.Timestamp.ShouldBe(record.Timestamp);
            deser.Signature.ShouldBeNull();
        });
    }

    [Fact]
    public void PingMessage_RoundTrips_Through_KademliaMessage()
    {
        MsgGen.PingMessageGen.Sample(ping =>
        {
            var bytes = ping.Serialize();
            var deser = (PingMessage)KademliaMessage.Deserialize(bytes);

            deser.MessageId.ShouldBe(ping.MessageId);
            deser.SenderId.ShouldBe(ping.SenderId);
            deser.SenderPublicKey.ShouldBe(ping.SenderPublicKey);
        });
    }

    [Fact]
    public void PongMessage_RoundTrips_Through_KademliaMessage()
    {
        MsgGen.PongMessageGen.Sample(pong =>
        {
            var bytes = pong.Serialize();
            var deser = (PongMessage)KademliaMessage.Deserialize(bytes);

            deser.MessageId.ShouldBe(pong.MessageId);
            deser.InResponseTo.ShouldBe(pong.InResponseTo);
            deser.SenderId.ShouldBe(pong.SenderId);
            deser.SenderPublicKey.ShouldBe(pong.SenderPublicKey);
        });
    }

    [Fact]
    public void FindNodeMessage_RoundTrips_Through_KademliaMessage()
    {
        MsgGen.FindNodeMessageGen.Sample(req =>
        {
            var bytes = req.Serialize();
            var deser = (FindNodeMessage)KademliaMessage.Deserialize(bytes);

            deser.MessageId.ShouldBe(req.MessageId);
            deser.TargetId.ShouldBe(req.TargetId);
        });
    }

    [Fact]
    public void StoreMessage_RoundTrips_Through_KademliaMessage()
    {
        MsgGen.StoreMessageGen.Sample(store =>
        {
            var bytes = store.Serialize();
            var deser = (StoreMessage)KademliaMessage.Deserialize(bytes);

            deser.MessageId.ShouldBe(store.MessageId);
            deser.Key.ShouldBe(store.Key);
            deser.Value.ShouldBe(store.Value);
            deser.TtlSeconds.ShouldBe(store.TtlSeconds);
            deser.Timestamp.ShouldBe(store.Timestamp);
            deser.SigningPublicKey.ShouldBe(store.SigningPublicKey);
            deser.Signature.ShouldBe(store.Signature);
        });
    }

    [Fact]
    public void StoreOfflineMessage_RoundTrips_Through_KademliaMessage()
    {
        MsgGen.StoreOfflineMessageGen.Sample(store =>
        {
            var bytes = store.Serialize();
            var deser = (StoreOfflineMessageMessage)KademliaMessage.Deserialize(bytes);

            deser.MessageId.ShouldBe(store.MessageId);
            deser.RecipientPublicKey.ShouldBe(store.RecipientPublicKey);
            deser.EncryptedMessage.ShouldBe(store.EncryptedMessage);
            deser.Timestamp.ShouldBe(store.Timestamp);
            deser.SigningPublicKey.ShouldBe(store.SigningPublicKey);
            deser.Signature.ShouldBe(store.Signature);
        });
    }

    [Fact]
    public void GetOfflineMessages_RoundTrips_Through_KademliaMessage()
    {
        MsgGen.GetOfflineMessagesGen.Sample(req =>
        {
            var bytes = req.Serialize();
            var deser = (GetOfflineMessagesMessage)KademliaMessage.Deserialize(bytes);

            deser.MessageId.ShouldBe(req.MessageId);
            deser.RecipientPublicKey.ShouldBe(req.RecipientPublicKey);
            deser.SigningPublicKey.ShouldBe(req.SigningPublicKey);
            deser.Timestamp.ShouldBe(req.Timestamp);
            deser.Signature.ShouldBe(req.Signature);
        });
    }

    [Fact]
    public void OnionMessageWrapper_RoundTrips_Through_KademliaMessage()
    {
        MsgGen.OnionMessageWrapperGen.Sample(wrap =>
        {
            var bytes = wrap.Serialize();
            var deser = (OnionMessageWrapper)KademliaMessage.Deserialize(bytes);

            deser.MessageId.ShouldBe(wrap.MessageId);
            deser.SenderId.ShouldBe(wrap.SenderId);
            deser.EncryptedPayload.ShouldBe(wrap.EncryptedPayload);
        });
    }

    [Fact]
    public void OnionLayer_RoundTrips()
    {
        MsgGen.OnionLayerGen.Sample(layer =>
        {
            var bytes = layer.Serialize();
            var deser = OnionLayer.Deserialize(bytes);

            deser.EphemeralPublicKey.ShouldBe(layer.EphemeralPublicKey);
            deser.Nonce.ShouldBe(layer.Nonce);
            deser.Ciphertext.ShouldBe(layer.Ciphertext);
        });
    }
}
