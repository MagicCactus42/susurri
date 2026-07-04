using NSec.Cryptography;
using Shouldly;
using Susurri.Modules.DHT.Core.Onion.GroupChat;
using Xunit;

namespace Susurri.Tests.Unit.Onion;

public class GroupRatchetTests
{
    private static (Key Encryption, byte[] EncryptionPub, Key Signing, byte[] SigningPub) MakeIdentity()
    {
        var encryption = Key.Create(KeyAgreementAlgorithm.X25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var signing = Key.Create(SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (encryption,
            encryption.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            signing,
            signing.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    }

    private static GroupInfo MakeGroup()
    {
        var groupKey = GroupKey.Create();
        return new GroupInfo { GroupId = groupKey.GroupId, Name = "test", Key = groupKey, IsOwner = true };
    }

    private static GroupSenderKeyDistribution MakeDistribution(
        GroupInfo group, GroupSendKeys keys,
        byte[] senderPub, Key signing, byte[] signingPub)
    {
        var distribution = new GroupSenderKeyDistribution
        {
            GroupId = group.GroupId,
            Generation = keys.Generation,
            Iteration = keys.Iteration,
            KeyVersion = keys.KeyVersion,
            ChainKey = keys.ChainKeySnapshot,
            SenderPublicKey = senderPub,
            SenderSigningPublicKey = signingPub,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        distribution.Signature = SignatureAlgorithm.Ed25519.Sign(signing, distribution.GetSignableData());
        return distribution;
    }

    [Fact]
    public void SenderKey_Distribution_And_Message_RoundTrip()
    {
        var alice = MakeIdentity();
        var bob = MakeIdentity();
        var group = MakeGroup();

        using var aliceRatchet = new GroupRatchetManager(null, alice.EncryptionPub);
        using var bobRatchet = new GroupRatchetManager(null, bob.EncryptionPub);

        var keys = aliceRatchet.PrepareSend(group);
        var distribution = MakeDistribution(group, keys, alice.EncryptionPub, alice.Signing, alice.SigningPub);

        var sealedBlob = distribution.SealFor(bob.EncryptionPub);
        var opened = GroupSenderKeyDistribution.OpenSealed(sealedBlob, bob.Encryption);

        opened.VerifySignature().ShouldBeTrue();
        opened.ChainKey.ShouldBe(keys.ChainKeySnapshot);
        bobRatchet.AcceptDistribution(group, opened);

        var message = new GroupMessage
        {
            GroupId = group.GroupId,
            SenderPublicKey = alice.EncryptionPub,
            Content = "forward secret hello",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        var envelope = EncryptedGroupMessageV2.Seal(
            message, keys.MessageKey, keys.Generation, keys.Iteration, keys.KeyVersion);

        var wire = envelope.Serialize();
        var parsed = EncryptedGroupMessageV2.Deserialize(wire);

        var messageKey = bobRatchet.TryTakeMessageKey(
            group, alice.EncryptionPub, parsed.Generation, parsed.Iteration, parsed.KeyVersion);
        messageKey.ShouldNotBeNull();

        var decrypted = parsed.Open(messageKey!);
        decrypted.Content.ShouldBe("forward secret hello");
        decrypted.SenderPublicKey.ShouldBe(alice.EncryptionPub);
    }

    [Fact]
    public void Consumed_MessageKey_Is_Erased()
    {
        var alice = MakeIdentity();
        var bob = MakeIdentity();
        var group = MakeGroup();

        using var aliceRatchet = new GroupRatchetManager(null, alice.EncryptionPub);
        using var bobRatchet = new GroupRatchetManager(null, bob.EncryptionPub);

        var keys = aliceRatchet.PrepareSend(group);
        var distribution = MakeDistribution(group, keys, alice.EncryptionPub, alice.Signing, alice.SigningPub);
        bobRatchet.AcceptDistribution(group, GroupSenderKeyDistribution.OpenSealed(
            distribution.SealFor(bob.EncryptionPub), bob.Encryption));

        bobRatchet.TryTakeMessageKey(group, alice.EncryptionPub, keys.Generation, keys.Iteration, keys.KeyVersion)
            .ShouldNotBeNull();
        bobRatchet.TryTakeMessageKey(group, alice.EncryptionPub, keys.Generation, keys.Iteration, keys.KeyVersion)
            .ShouldBeNull();
    }

    [Fact]
    public void OutOfOrder_Messages_Use_Skipped_Keys()
    {
        var alice = MakeIdentity();
        var bob = MakeIdentity();
        var group = MakeGroup();

        using var aliceRatchet = new GroupRatchetManager(null, alice.EncryptionPub);
        using var bobRatchet = new GroupRatchetManager(null, bob.EncryptionPub);

        var keys0 = aliceRatchet.PrepareSend(group);
        var keys1 = aliceRatchet.PrepareSend(group);
        var keys2 = aliceRatchet.PrepareSend(group);

        var distribution = MakeDistribution(group, keys0, alice.EncryptionPub, alice.Signing, alice.SigningPub);
        bobRatchet.AcceptDistribution(group, GroupSenderKeyDistribution.OpenSealed(
            distribution.SealFor(bob.EncryptionPub), bob.Encryption));

        var key2 = bobRatchet.TryTakeMessageKey(group, alice.EncryptionPub, keys2.Generation, keys2.Iteration, keys2.KeyVersion);
        key2.ShouldNotBeNull();
        key2.ShouldBe(keys2.MessageKey);

        var key0 = bobRatchet.TryTakeMessageKey(group, alice.EncryptionPub, keys0.Generation, keys0.Iteration, keys0.KeyVersion);
        key0.ShouldNotBeNull();
        key0.ShouldBe(keys0.MessageKey);

        var key1 = bobRatchet.TryTakeMessageKey(group, alice.EncryptionPub, keys1.Generation, keys1.Iteration, keys1.KeyVersion);
        key1.ShouldNotBeNull();
        key1.ShouldBe(keys1.MessageKey);

        bobRatchet.TryTakeMessageKey(group, alice.EncryptionPub, keys1.Generation, keys1.Iteration, keys1.KeyVersion)
            .ShouldBeNull();
    }

    [Fact]
    public void Stale_Distribution_Does_Not_Roll_Back_The_Chain()
    {
        var alice = MakeIdentity();
        var bob = MakeIdentity();
        var group = MakeGroup();

        using var aliceRatchet = new GroupRatchetManager(null, alice.EncryptionPub);
        using var bobRatchet = new GroupRatchetManager(null, bob.EncryptionPub);

        var keys = aliceRatchet.PrepareSend(group);
        var distribution = MakeDistribution(group, keys, alice.EncryptionPub, alice.Signing, alice.SigningPub);
        var opened = GroupSenderKeyDistribution.OpenSealed(
            distribution.SealFor(bob.EncryptionPub), bob.Encryption);

        bobRatchet.AcceptDistribution(group, opened);
        bobRatchet.TryTakeMessageKey(group, alice.EncryptionPub, keys.Generation, keys.Iteration, keys.KeyVersion)
            .ShouldNotBeNull();

        bobRatchet.AcceptDistribution(group, opened);
        bobRatchet.TryTakeMessageKey(group, alice.EncryptionPub, keys.Generation, keys.Iteration, keys.KeyVersion)
            .ShouldBeNull();
    }

    [Fact]
    public void KeyVersion_Mismatch_Yields_No_Key()
    {
        var alice = MakeIdentity();
        var bob = MakeIdentity();
        var group = MakeGroup();

        using var aliceRatchet = new GroupRatchetManager(null, alice.EncryptionPub);
        using var bobRatchet = new GroupRatchetManager(null, bob.EncryptionPub);

        var keys = aliceRatchet.PrepareSend(group);
        var distribution = MakeDistribution(group, keys, alice.EncryptionPub, alice.Signing, alice.SigningPub);
        bobRatchet.AcceptDistribution(group, GroupSenderKeyDistribution.OpenSealed(
            distribution.SealFor(bob.EncryptionPub), bob.Encryption));

        bobRatchet.TryTakeMessageKey(group, alice.EncryptionPub, keys.Generation, keys.Iteration, keys.KeyVersion + 1)
            .ShouldBeNull();
    }

    [Fact]
    public void Rotation_Resets_The_Sender_Chain()
    {
        var alice = MakeIdentity();
        var group = MakeGroup();

        using var aliceRatchet = new GroupRatchetManager(null, alice.EncryptionPub);

        var before = aliceRatchet.PrepareSend(group);
        group.Key = group.Key.Rotate();
        var after = aliceRatchet.PrepareSend(group);

        after.KeyVersion.ShouldBe(before.KeyVersion + 1);
        after.Iteration.ShouldBe(0u);
        after.Generation.ShouldBeGreaterThan(before.Generation);
        after.ChainKeySnapshot.ShouldNotBe(before.ChainKeySnapshot);
    }

    [Fact]
    public void Sender_Chain_Rekeys_After_Message_Epoch()
    {
        var alice = MakeIdentity();
        var group = MakeGroup();

        using var aliceRatchet = new GroupRatchetManager(null, alice.EncryptionPub);

        var first = aliceRatchet.PrepareSend(group);
        for (var i = 1u; i < GroupRatchetManager.SenderEpochMessages; i++)
            aliceRatchet.PrepareSend(group);

        var next = aliceRatchet.PrepareSend(group);
        next.Generation.ShouldBeGreaterThan(first.Generation);
        next.Iteration.ShouldBe(0u);
        next.ChainKeySnapshot.ShouldNotBe(first.ChainKeySnapshot);
    }

    [Fact]
    public void Tampered_Distribution_Fails_Signature_Check()
    {
        var alice = MakeIdentity();
        var group = MakeGroup();

        using var aliceRatchet = new GroupRatchetManager(null, alice.EncryptionPub);
        var keys = aliceRatchet.PrepareSend(group);
        var distribution = MakeDistribution(group, keys, alice.EncryptionPub, alice.Signing, alice.SigningPub);

        var tampered = new GroupSenderKeyDistribution
        {
            GroupId = distribution.GroupId,
            Generation = distribution.Generation + 1,
            Iteration = distribution.Iteration,
            KeyVersion = distribution.KeyVersion,
            ChainKey = distribution.ChainKey,
            SenderPublicKey = distribution.SenderPublicKey,
            SenderSigningPublicKey = distribution.SenderSigningPublicKey,
            Timestamp = distribution.Timestamp,
            Signature = distribution.Signature
        };

        distribution.VerifySignature().ShouldBeTrue();
        tampered.VerifySignature().ShouldBeFalse();
    }

    [Fact]
    public void Tampered_Ciphertext_Fails_To_Open()
    {
        var alice = MakeIdentity();
        var group = MakeGroup();

        using var aliceRatchet = new GroupRatchetManager(null, alice.EncryptionPub);
        var keys = aliceRatchet.PrepareSend(group);

        var message = new GroupMessage
        {
            GroupId = group.GroupId,
            SenderPublicKey = alice.EncryptionPub,
            Content = "secret",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        var envelope = EncryptedGroupMessageV2.Seal(
            message, keys.MessageKey, keys.Generation, keys.Iteration, keys.KeyVersion);

        envelope.Ciphertext[0] ^= 0xFF;
        Should.Throw<Exception>(() => envelope.Open(keys.MessageKey));
    }
}
