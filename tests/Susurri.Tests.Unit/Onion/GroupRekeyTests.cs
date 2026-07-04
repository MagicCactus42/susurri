using NSec.Cryptography;
using Shouldly;
using Susurri.Modules.DHT.Core.Onion.GroupChat;
using Xunit;

namespace Susurri.Tests.Unit.Onion;

public class GroupRekeyTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "susurri-tests", Guid.NewGuid().ToString("N"));
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
            }
        }
    }

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

    private static GroupRekeyMessage SignedRekey(
        GroupInfo group, byte[] recipientPublicKey, Key signing, byte[] signingPub)
    {
        var rekey = new GroupRekeyMessage
        {
            GroupId = group.GroupId,
            Wrapped = group.Key.WrapForMember(recipientPublicKey),
            Roster = group.Members.ToList(),
            OwnerSigningPublicKey = signingPub,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        rekey.Signature = SignatureAlgorithm.Ed25519.Sign(signing, rekey.GetSignableData());
        return rekey;
    }

    [Fact]
    public void Rekey_RoundTrips_Wire_And_Applies_On_The_Member()
    {
        var owner = MakeIdentity();
        var member = MakeIdentity();

        using var ownerManager = new GroupManager(owner.Encryption, null, owner.SigningPub, TempDir());
        var group = ownerManager.CreateGroup("g");
        ownerManager.AddMember(group.GroupId, member.EncryptionPub);

        using var memberManager = new GroupManager(member.Encryption, null, member.SigningPub, TempDir());
        var joined = memberManager.JoinGroup(group.Key.WrapForMember(member.EncryptionPub), "g", owner.SigningPub);
        joined.ShouldNotBeNull();
        joined!.Key.Version.ShouldBe(1);

        ownerManager.RotateKey(group.GroupId);
        var rekey = SignedRekey(group, member.EncryptionPub, owner.Signing, owner.SigningPub);

        var wire = GroupRekeyMessage.Deserialize(rekey.Serialize());
        wire.VerifySignature().ShouldBeTrue();

        var applied = memberManager.ApplyRekey(wire);
        applied.ShouldNotBeNull();
        applied!.Key.Version.ShouldBe(2);
        applied.Key.SymmetricKey.ShouldBe(group.Key.SymmetricKey);
        applied.Members.Count.ShouldBe(2);

        memberManager.ApplyRekey(wire).ShouldBeNull();
    }

    [Fact]
    public void Rekey_From_A_NonOwner_Is_Rejected()
    {
        var owner = MakeIdentity();
        var member = MakeIdentity();
        var mallory = MakeIdentity();

        using var ownerManager = new GroupManager(owner.Encryption, null, owner.SigningPub, TempDir());
        var group = ownerManager.CreateGroup("g");

        using var memberManager = new GroupManager(member.Encryption, null, member.SigningPub, TempDir());
        memberManager.JoinGroup(group.Key.WrapForMember(member.EncryptionPub), "g", owner.SigningPub)
            .ShouldNotBeNull();

        var hijacked = new GroupInfo
        {
            GroupId = group.GroupId,
            Name = "g",
            Key = group.Key.Rotate(),
            Members = group.Members
        };

        var wrongIdentity = SignedRekey(hijacked, member.EncryptionPub, mallory.Signing, mallory.SigningPub);
        memberManager.ApplyRekey(wrongIdentity).ShouldBeNull();

        var forgedSignature = SignedRekey(hijacked, member.EncryptionPub, mallory.Signing, owner.SigningPub);
        memberManager.ApplyRekey(forgedSignature).ShouldBeNull();
    }

    [Fact]
    public void Rekey_On_A_Legacy_Group_Without_Owner_Identity_Is_Rejected()
    {
        var owner = MakeIdentity();
        var member = MakeIdentity();

        using var ownerManager = new GroupManager(owner.Encryption, null, owner.SigningPub, TempDir());
        var group = ownerManager.CreateGroup("g");

        using var memberManager = new GroupManager(member.Encryption, null, member.SigningPub, TempDir());
        memberManager.JoinGroup(group.Key.WrapForMember(member.EncryptionPub), "g")
            .ShouldNotBeNull();

        ownerManager.RotateKey(group.GroupId);
        var rekey = SignedRekey(group, member.EncryptionPub, owner.Signing, owner.SigningPub);

        memberManager.ApplyRekey(rekey).ShouldBeNull();
    }

    [Fact]
    public void Rollback_To_An_Older_Version_Is_Rejected()
    {
        var owner = MakeIdentity();
        var member = MakeIdentity();

        using var ownerManager = new GroupManager(owner.Encryption, null, owner.SigningPub, TempDir());
        var group = ownerManager.CreateGroup("g");

        using var memberManager = new GroupManager(member.Encryption, null, member.SigningPub, TempDir());
        memberManager.JoinGroup(group.Key.WrapForMember(member.EncryptionPub), "g", owner.SigningPub)
            .ShouldNotBeNull();

        ownerManager.RotateKey(group.GroupId);
        var rekeyV2 = SignedRekey(group, member.EncryptionPub, owner.Signing, owner.SigningPub);

        ownerManager.RotateKey(group.GroupId);
        var rekeyV3 = SignedRekey(group, member.EncryptionPub, owner.Signing, owner.SigningPub);

        memberManager.ApplyRekey(rekeyV3).ShouldNotBeNull();
        memberManager.ApplyRekey(rekeyV2).ShouldBeNull();
    }
}
