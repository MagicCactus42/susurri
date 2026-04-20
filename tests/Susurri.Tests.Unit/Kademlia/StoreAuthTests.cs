using NSec.Cryptography;
using Shouldly;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Kademlia.Protocol;
using Xunit;

namespace Susurri.Tests.Unit.Kademlia;

public class StoreAuthTests
{
    private readonly Key _signingKey;
    private readonly byte[] _signingPublicKey;
    private readonly byte[] _encryptionPublicKey;

    public StoreAuthTests()
    {
        _signingKey = Key.Create(SignatureAlgorithm.Ed25519);
        _signingPublicKey = _signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        using var encKey = Key.Create(KeyAgreementAlgorithm.X25519);
        _encryptionPublicKey = encKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    [Fact]
    public void StoreMessage_SignableData_IsStable()
    {
        var msg = CreateUnsignedStore(KademliaId.Random(), new byte[] { 1, 2, 3 }, 3600);
        msg.GetSignableData().ShouldBe(msg.GetSignableData());
    }

    [Fact]
    public void StoreMessage_SignableData_DiffersForDifferentValues()
    {
        var key = KademliaId.Random();
        var sharedMessageId = Guid.NewGuid();
        var sharedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var msg1 = new StoreMessage
        {
            MessageId = sharedMessageId,
            SenderId = KademliaId.FromPublicKey(_encryptionPublicKey),
            SenderPublicKey = _encryptionPublicKey,
            Key = key,
            Value = new byte[] { 1, 2, 3 },
            TtlSeconds = 3600,
            Timestamp = sharedTimestamp,
            SigningPublicKey = _signingPublicKey
        };

        var msg2 = new StoreMessage
        {
            MessageId = sharedMessageId,
            SenderId = KademliaId.FromPublicKey(_encryptionPublicKey),
            SenderPublicKey = _encryptionPublicKey,
            Key = key,
            Value = new byte[] { 4, 5, 6 },
            TtlSeconds = 3600,
            Timestamp = sharedTimestamp,
            SigningPublicKey = _signingPublicKey
        };

        msg1.GetSignableData().ShouldNotBe(msg2.GetSignableData());
    }

    [Fact]
    public void StoreMessage_RoundTrip_PreservesNewFields()
    {
        var msg = CreateSignedStore(KademliaId.Random(), new byte[] { 1, 2, 3 }, 3600);

        var serialized = msg.Serialize();
        var deserialized = (StoreMessage)KademliaMessage.Deserialize(serialized);

        deserialized.Timestamp.ShouldBe(msg.Timestamp);
        deserialized.SigningPublicKey.ShouldBe(msg.SigningPublicKey);
        deserialized.Signature.ShouldBe(msg.Signature);
    }

    [Fact]
    public void StoreMessage_RoundTrip_SignatureStillValid()
    {
        var msg = CreateSignedStore(KademliaId.Random(), new byte[] { 1, 2, 3 }, 3600);

        var serialized = msg.Serialize();
        var deserialized = (StoreMessage)KademliaMessage.Deserialize(serialized);

        var sigPubKey = PublicKey.Import(
            SignatureAlgorithm.Ed25519, deserialized.SigningPublicKey, KeyBlobFormat.RawPublicKey);

        SignatureAlgorithm.Ed25519
            .Verify(sigPubKey, deserialized.GetSignableData(), deserialized.Signature)
            .ShouldBeTrue();
    }

    [Fact]
    public void StoreMessage_TamperedValue_FailsSignatureCheck()
    {
        var msg = CreateSignedStore(KademliaId.Random(), new byte[] { 1, 2, 3 }, 3600);

        var tampered = new StoreMessage
        {
            MessageId = msg.MessageId,
            SenderId = msg.SenderId,
            SenderPublicKey = msg.SenderPublicKey,
            Key = msg.Key,
            Value = new byte[] { 9, 9, 9 },
            TtlSeconds = msg.TtlSeconds,
            Timestamp = msg.Timestamp,
            SigningPublicKey = msg.SigningPublicKey,
            Signature = msg.Signature
        };

        var sigPubKey = PublicKey.Import(
            SignatureAlgorithm.Ed25519, tampered.SigningPublicKey, KeyBlobFormat.RawPublicKey);

        SignatureAlgorithm.Ed25519
            .Verify(sigPubKey, tampered.GetSignableData(), tampered.Signature)
            .ShouldBeFalse();
    }

    [Fact]
    public void StoreOfflineMessage_RoundTrip_PreservesNewFields()
    {
        var msg = CreateSignedStoreOffline(_encryptionPublicKey, new byte[] { 1, 2, 3, 4 });

        var serialized = msg.Serialize();
        var deserialized = (StoreOfflineMessageMessage)KademliaMessage.Deserialize(serialized);

        deserialized.Timestamp.ShouldBe(msg.Timestamp);
        deserialized.SigningPublicKey.ShouldBe(msg.SigningPublicKey);
        deserialized.Signature.ShouldBe(msg.Signature);
        deserialized.RecipientPublicKey.ShouldBe(msg.RecipientPublicKey);
        deserialized.EncryptedMessage.ShouldBe(msg.EncryptedMessage);
    }

    [Fact]
    public void StoreOfflineMessage_TamperedRecipient_FailsSignatureCheck()
    {
        var msg = CreateSignedStoreOffline(_encryptionPublicKey, new byte[] { 1, 2, 3, 4 });

        using var otherKey = Key.Create(KeyAgreementAlgorithm.X25519);
        var otherPubKey = otherKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var tampered = new StoreOfflineMessageMessage
        {
            MessageId = msg.MessageId,
            SenderId = msg.SenderId,
            SenderPublicKey = msg.SenderPublicKey,
            RecipientPublicKey = otherPubKey,
            EncryptedMessage = msg.EncryptedMessage,
            Timestamp = msg.Timestamp,
            SigningPublicKey = msg.SigningPublicKey,
            Signature = msg.Signature
        };

        var sigPubKey = PublicKey.Import(
            SignatureAlgorithm.Ed25519, tampered.SigningPublicKey, KeyBlobFormat.RawPublicKey);

        SignatureAlgorithm.Ed25519
            .Verify(sigPubKey, tampered.GetSignableData(), tampered.Signature)
            .ShouldBeFalse();
    }

    [Fact]
    public void StoreOfflineMessage_TamperedPayload_FailsSignatureCheck()
    {
        var msg = CreateSignedStoreOffline(_encryptionPublicKey, new byte[] { 1, 2, 3, 4 });

        var tampered = new StoreOfflineMessageMessage
        {
            MessageId = msg.MessageId,
            SenderId = msg.SenderId,
            SenderPublicKey = msg.SenderPublicKey,
            RecipientPublicKey = msg.RecipientPublicKey,
            EncryptedMessage = new byte[] { 9, 9, 9, 9 },
            Timestamp = msg.Timestamp,
            SigningPublicKey = msg.SigningPublicKey,
            Signature = msg.Signature
        };

        var sigPubKey = PublicKey.Import(
            SignatureAlgorithm.Ed25519, tampered.SigningPublicKey, KeyBlobFormat.RawPublicKey);

        SignatureAlgorithm.Ed25519
            .Verify(sigPubKey, tampered.GetSignableData(), tampered.Signature)
            .ShouldBeFalse();
    }

    private StoreMessage CreateUnsignedStore(KademliaId key, byte[] value, uint ttlSeconds)
    {
        return new StoreMessage
        {
            SenderId = KademliaId.FromPublicKey(_encryptionPublicKey),
            SenderPublicKey = _encryptionPublicKey,
            Key = key,
            Value = value,
            TtlSeconds = ttlSeconds,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            SigningPublicKey = _signingPublicKey
        };
    }

    private StoreMessage CreateSignedStore(KademliaId key, byte[] value, uint ttlSeconds)
    {
        var draft = CreateUnsignedStore(key, value, ttlSeconds);
        var signature = SignatureAlgorithm.Ed25519.Sign(_signingKey, draft.GetSignableData());

        return new StoreMessage
        {
            MessageId = draft.MessageId,
            SenderId = draft.SenderId,
            SenderPublicKey = draft.SenderPublicKey,
            Key = draft.Key,
            Value = draft.Value,
            TtlSeconds = draft.TtlSeconds,
            Timestamp = draft.Timestamp,
            SigningPublicKey = draft.SigningPublicKey,
            Signature = signature
        };
    }

    private StoreOfflineMessageMessage CreateSignedStoreOffline(byte[] recipientPublicKey, byte[] payload)
    {
        var draft = new StoreOfflineMessageMessage
        {
            SenderId = KademliaId.FromPublicKey(_encryptionPublicKey),
            SenderPublicKey = _encryptionPublicKey,
            RecipientPublicKey = recipientPublicKey,
            EncryptedMessage = payload,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            SigningPublicKey = _signingPublicKey
        };

        var signature = SignatureAlgorithm.Ed25519.Sign(_signingKey, draft.GetSignableData());

        return new StoreOfflineMessageMessage
        {
            MessageId = draft.MessageId,
            SenderId = draft.SenderId,
            SenderPublicKey = draft.SenderPublicKey,
            RecipientPublicKey = draft.RecipientPublicKey,
            EncryptedMessage = draft.EncryptedMessage,
            Timestamp = draft.Timestamp,
            SigningPublicKey = draft.SigningPublicKey,
            Signature = signature
        };
    }
}
