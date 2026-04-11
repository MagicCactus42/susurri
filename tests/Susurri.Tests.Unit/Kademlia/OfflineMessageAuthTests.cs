using NSec.Cryptography;
using Shouldly;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Kademlia.Protocol;
using Xunit;

namespace Susurri.Tests.Unit.Kademlia;

public class OfflineMessageAuthTests
{
    private readonly Key _signingKey;
    private readonly byte[] _signingPublicKey;
    private readonly byte[] _encryptionPublicKey;

    public OfflineMessageAuthTests()
    {
        _signingKey = Key.Create(SignatureAlgorithm.Ed25519);
        _signingPublicKey = _signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        using var encKey = Key.Create(KeyAgreementAlgorithm.X25519);
        _encryptionPublicKey = encKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    [Fact]
    public void GetSignableData_IsStable()
    {
        var msg = new GetOfflineMessagesMessage
        {
            RecipientPublicKey = _encryptionPublicKey,
            Timestamp = 1700000000L
        };

        var data1 = msg.GetSignableData();
        var data2 = msg.GetSignableData();

        data1.ShouldBe(data2);
    }

    [Fact]
    public void GetSignableData_IncludesRecipientKeyAndTimestamp()
    {
        var msg = new GetOfflineMessagesMessage
        {
            RecipientPublicKey = _encryptionPublicKey,
            Timestamp = 1700000000L
        };

        var data = msg.GetSignableData();

        data.Length.ShouldBe(32 + 8 + 16);
    }

    [Fact]
    public void GetSignableData_DiffersForDifferentRecipientKeys()
    {
        using var otherKey = Key.Create(KeyAgreementAlgorithm.X25519);
        var otherPubKey = otherKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var messageId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var msg1 = new GetOfflineMessagesMessage
        {
            MessageId = messageId,
            RecipientPublicKey = _encryptionPublicKey,
            Timestamp = timestamp
        };

        var msg2 = new GetOfflineMessagesMessage
        {
            MessageId = messageId,
            RecipientPublicKey = otherPubKey,
            Timestamp = timestamp
        };

        msg1.GetSignableData().ShouldNotBe(msg2.GetSignableData());
    }

    [Fact]
    public void GetSignableData_DiffersForDifferentTimestamps()
    {
        var messageId = Guid.NewGuid();

        var msg1 = new GetOfflineMessagesMessage
        {
            MessageId = messageId,
            RecipientPublicKey = _encryptionPublicKey,
            Timestamp = 1700000000L
        };

        var msg2 = new GetOfflineMessagesMessage
        {
            MessageId = messageId,
            RecipientPublicKey = _encryptionPublicKey,
            Timestamp = 1700000001L
        };

        msg1.GetSignableData().ShouldNotBe(msg2.GetSignableData());
    }

    [Fact]
    public void ValidSignature_IsVerifiable()
    {
        var msg = CreateSignedRequest(_signingKey, _encryptionPublicKey);

        var sigPubKey = PublicKey.Import(
            SignatureAlgorithm.Ed25519, msg.SigningPublicKey, KeyBlobFormat.RawPublicKey);

        var valid = SignatureAlgorithm.Ed25519.Verify(
            sigPubKey, msg.GetSignableData(), msg.Signature);

        valid.ShouldBeTrue();
    }

    [Fact]
    public void WrongSigningKey_FailsVerification()
    {
        var msg = CreateSignedRequest(_signingKey, _encryptionPublicKey);

        using var wrongKey = Key.Create(SignatureAlgorithm.Ed25519);
        var wrongPubKey = PublicKey.Import(
            SignatureAlgorithm.Ed25519,
            wrongKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            KeyBlobFormat.RawPublicKey);

        var valid = SignatureAlgorithm.Ed25519.Verify(
            wrongPubKey, msg.GetSignableData(), msg.Signature);

        valid.ShouldBeFalse();
    }

    [Fact]
    public void TamperedRecipientKey_FailsVerification()
    {
        var msg = CreateSignedRequest(_signingKey, _encryptionPublicKey);

        using var otherKey = Key.Create(KeyAgreementAlgorithm.X25519);
        var tamperedMsg = new GetOfflineMessagesMessage
        {
            MessageId = msg.MessageId,
            RecipientPublicKey = otherKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            SigningPublicKey = msg.SigningPublicKey,
            Timestamp = msg.Timestamp,
            Signature = msg.Signature
        };

        var sigPubKey = PublicKey.Import(
            SignatureAlgorithm.Ed25519, tamperedMsg.SigningPublicKey, KeyBlobFormat.RawPublicKey);

        var valid = SignatureAlgorithm.Ed25519.Verify(
            sigPubKey, tamperedMsg.GetSignableData(), tamperedMsg.Signature);

        valid.ShouldBeFalse();
    }

    [Fact]
    public void TamperedTimestamp_FailsVerification()
    {
        var msg = CreateSignedRequest(_signingKey, _encryptionPublicKey);

        var tamperedMsg = new GetOfflineMessagesMessage
        {
            MessageId = msg.MessageId,
            RecipientPublicKey = msg.RecipientPublicKey,
            SigningPublicKey = msg.SigningPublicKey,
            Timestamp = msg.Timestamp + 1,
            Signature = msg.Signature
        };

        var sigPubKey = PublicKey.Import(
            SignatureAlgorithm.Ed25519, tamperedMsg.SigningPublicKey, KeyBlobFormat.RawPublicKey);

        var valid = SignatureAlgorithm.Ed25519.Verify(
            sigPubKey, tamperedMsg.GetSignableData(), tamperedMsg.Signature);

        valid.ShouldBeFalse();
    }

    [Fact]
    public void Serialization_RoundTrip_PreservesAllFields()
    {
        var msg = CreateSignedRequest(_signingKey, _encryptionPublicKey);

        var serialized = msg.Serialize();
        var deserialized = (GetOfflineMessagesMessage)KademliaMessage.Deserialize(serialized);

        deserialized.MessageId.ShouldBe(msg.MessageId);
        deserialized.RecipientPublicKey.ShouldBe(msg.RecipientPublicKey);
        deserialized.SigningPublicKey.ShouldBe(msg.SigningPublicKey);
        deserialized.Timestamp.ShouldBe(msg.Timestamp);
        deserialized.Signature.ShouldBe(msg.Signature);
    }

    [Fact]
    public void Serialization_RoundTrip_SignatureStillValid()
    {
        var msg = CreateSignedRequest(_signingKey, _encryptionPublicKey);

        var serialized = msg.Serialize();
        var deserialized = (GetOfflineMessagesMessage)KademliaMessage.Deserialize(serialized);

        var sigPubKey = PublicKey.Import(
            SignatureAlgorithm.Ed25519, deserialized.SigningPublicKey, KeyBlobFormat.RawPublicKey);

        var valid = SignatureAlgorithm.Ed25519.Verify(
            sigPubKey, deserialized.GetSignableData(), deserialized.Signature);

        valid.ShouldBeTrue();
    }

    [Fact]
    public void Serialization_EmptySignature_RoundTrip()
    {
        var msg = new GetOfflineMessagesMessage
        {
            SenderId = KademliaId.Random(),
            SenderPublicKey = _encryptionPublicKey,
            RecipientPublicKey = _encryptionPublicKey,
            SigningPublicKey = Array.Empty<byte>(),
            Timestamp = 0,
            Signature = Array.Empty<byte>()
        };

        var serialized = msg.Serialize();
        var deserialized = (GetOfflineMessagesMessage)KademliaMessage.Deserialize(serialized);

        deserialized.SigningPublicKey.ShouldBeEmpty();
        deserialized.Signature.ShouldBeEmpty();
        deserialized.Timestamp.ShouldBe(0);
    }

    private static GetOfflineMessagesMessage CreateSignedRequest(Key signingKey, byte[] recipientPublicKey)
    {
        var signingPubKey = signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var request = new GetOfflineMessagesMessage
        {
            SenderId = KademliaId.FromPublicKey(recipientPublicKey),
            SenderPublicKey = recipientPublicKey,
            RecipientPublicKey = recipientPublicKey,
            SigningPublicKey = signingPubKey,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var signature = SignatureAlgorithm.Ed25519.Sign(signingKey, request.GetSignableData());

        return new GetOfflineMessagesMessage
        {
            MessageId = request.MessageId,
            SenderId = request.SenderId,
            SenderPublicKey = request.SenderPublicKey,
            RecipientPublicKey = request.RecipientPublicKey,
            SigningPublicKey = request.SigningPublicKey,
            Timestamp = request.Timestamp,
            Signature = signature
        };
    }
}
