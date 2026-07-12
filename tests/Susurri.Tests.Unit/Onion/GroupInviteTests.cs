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

        var groupKey = GroupKey.Create();
        var wrapped = groupKey.WrapForMember(memberPub);

        var code = GroupInvite.Encode("book club", wrapped);
        var (name, decoded) = GroupInvite.Decode(code);

        name.ShouldBe("book club");
        decoded.GroupId.ShouldBe(groupKey.GroupId);

        var recovered = GroupKey.UnwrapWithPrivateKey(decoded, member);
        recovered.SymmetricKey.ShouldBe(groupKey.SymmetricKey);
        recovered.GroupId.ShouldBe(groupKey.GroupId);
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
