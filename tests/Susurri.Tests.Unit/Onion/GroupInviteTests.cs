using NSec.Cryptography;
using Shouldly;
using Susurri.Modules.DHT.Core.Onion.GroupChat;
using Xunit;

namespace Susurri.Tests.Unit.Onion;

public class GroupInviteTests
{
    [Fact]
    public void Invite_RoundTrips_And_Recovers_The_Group_Key()
    {
        using var member = Key.Create(KeyAgreementAlgorithm.X25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var memberPub = member.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        using var ownerSigning = Key.Create(SignatureAlgorithm.Ed25519);
        var ownerSigningPub = ownerSigning.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var groupKey = GroupKey.Create();
        var wrapped = groupKey.WrapForMember(memberPub);

        var code = GroupInvite.Encode("book club", wrapped, ownerSigningPub);
        var (name, decoded, owner) = GroupInvite.Decode(code);

        name.ShouldBe("book club");
        decoded.GroupId.ShouldBe(groupKey.GroupId);
        owner.ShouldBe(ownerSigningPub);

        var recovered = GroupKey.UnwrapWithPrivateKey(decoded, member);
        recovered.SymmetricKey.ShouldBe(groupKey.SymmetricKey);
        recovered.GroupId.ShouldBe(groupKey.GroupId);
    }

    [Fact]
    public void Legacy_Invite_Without_Owner_Identity_Still_Decodes()
    {
        using var member = Key.Create(KeyAgreementAlgorithm.X25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var memberPub = member.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var groupKey = GroupKey.Create();
        var keyBytes = groupKey.WrapForMember(memberPub).Serialize();

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write("old group");
        writer.Write(keyBytes.Length);
        writer.Write(keyBytes);
        writer.Flush();

        var (name, decoded, owner) = GroupInvite.Decode(Convert.ToBase64String(ms.ToArray()));

        name.ShouldBe("old group");
        decoded.GroupId.ShouldBe(groupKey.GroupId);
        owner.ShouldBeEmpty();
    }

    [Fact]
    public void Malformed_Invite_Throws()
    {
        Should.Throw<Exception>(() => GroupInvite.Decode("not-valid-base64!!!"));
    }

    [Fact]
    public void GroupMessage_Unpadded_RoundTrips()
    {
        var groupKey = GroupKey.Create();
        var message = new GroupMessage
        {
            GroupId = groupKey.GroupId,
            SenderPublicKey = new byte[32],
            Content = "hello group",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var encrypted = message.EncryptUnpadded(groupKey);
        var decrypted = GroupMessage.DecryptUnpadded(encrypted, groupKey);

        decrypted.Content.ShouldBe("hello group");
        decrypted.MessageId.ShouldBe(message.MessageId);
    }
}
