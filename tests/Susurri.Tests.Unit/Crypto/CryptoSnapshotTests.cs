using NSec.Cryptography;
using Shouldly;
using Susurri.Modules.IAM.Core.Crypto;
using Susurri.Shared.Abstractions.Security;

namespace Susurri.Tests.Unit.Crypto;

// Phase 3.6 regression guards. Pin the *actual byte values* produced by the
// crypto pipeline so any silent drift in PBKDF2 iterations, hash algorithm,
// seed split, or HKDF context strings fails the build instead of silently
// invalidating every existing user identity in the network.
public class CryptoSnapshotTests
{
    private const string SnapshotPassphrase = "snapshot-vector-passphrase";

    private static byte[] SnapshotSalt()
    {
        var salt = new byte[CryptoKeyGenerator.SaltSize];
        for (int i = 0; i < salt.Length; i++)
            salt[i] = (byte)i;
        return salt;
    }

    [Fact]
    public void Pbkdf2KeyDerivation_ProducesPinnedEd25519PublicKey()
    {
        // Snapshot vector — DO NOT update without coordinating a network-wide migration.
        // Changing PBKDF2 iterations, hash algorithm, seed size, or the
        // signing-key byte slice will cause every existing identity to derive
        // different keys, and therefore lose access. This test exists to make
        // such a change loud, not silent.
        const string ExpectedSigningPublicKeyHex =
            "006296BC352CC2AD817CB4CBF10A9CCD77F49B38A1EB4C71A241B7BBB472D774";

        using var keyPair = new CryptoKeyGenerator()
            .GenerateKeyPair(SnapshotPassphrase, SnapshotSalt());

        Convert.ToHexString(keyPair.SigningPublicKey)
            .ShouldBe(ExpectedSigningPublicKeyHex);
    }

    [Fact]
    public void Pbkdf2KeyDerivation_ProducesPinnedX25519PublicKey()
    {
        const string ExpectedEncryptionPublicKeyHex =
            "DED18822A8077694E93B4B4C996A1F7F7BAC90FA0A3E25F7F8F0756710734074";

        using var keyPair = new CryptoKeyGenerator()
            .GenerateKeyPair(SnapshotPassphrase, SnapshotSalt());

        Convert.ToHexString(keyPair.EncryptionPublicKey)
            .ShouldBe(ExpectedEncryptionPublicKeyHex);
    }

    // HKDF info strings are part of the wire-format contract: any change
    // would make currently-deployed nodes derive different keys and see every
    // existing packet as undecryptable garbage. Hex is pinned as a literal so
    // that tampering with the source string AND tampering with the ASCII
    // mirror in the test together still fails on the hex check.

    [Fact]
    public void HkdfContext_OnionLayer_HasPinnedByteValue()
    {
        const string ExpectedAscii = "susurri-onion-layer-v1";
        const string ExpectedHex = "737573757272692D6F6E696F6E2D6C617965722D7631";

        var actual = HkdfContexts.OnionLayer.ToArray();

        actual.Length.ShouldBe(ExpectedAscii.Length);
        System.Text.Encoding.ASCII.GetString(actual).ShouldBe(ExpectedAscii);
        Convert.ToHexString(actual).ShouldBe(ExpectedHex);
    }

    [Fact]
    public void HkdfContext_DirectMessage_HasPinnedByteValue()
    {
        const string ExpectedAscii = "susurri-direct-message-v1";
        const string ExpectedHex = "737573757272692D6469726563742D6D6573736167652D7631";

        var actual = HkdfContexts.DirectMessage.ToArray();

        actual.Length.ShouldBe(ExpectedAscii.Length);
        System.Text.Encoding.ASCII.GetString(actual).ShouldBe(ExpectedAscii);
        Convert.ToHexString(actual).ShouldBe(ExpectedHex);
    }

    [Fact]
    public void HkdfContext_GroupKeyWrap_HasPinnedByteValue()
    {
        const string ExpectedAscii = "susurri-group-key-wrap-v1";
        const string ExpectedHex = "737573757272692D67726F75702D6B65792D777261702D7631";

        var actual = HkdfContexts.GroupKeyWrap.ToArray();

        actual.Length.ShouldBe(ExpectedAscii.Length);
        System.Text.Encoding.ASCII.GetString(actual).ShouldBe(ExpectedAscii);
        Convert.ToHexString(actual).ShouldBe(ExpectedHex);
    }

    [Fact]
    public void HkdfContexts_AreDistinct_NoCollisionBetweenDomains()
    {
        // Domain separation is the entire point of having multiple contexts.
        // If two contexts ever collide, a key derived for one purpose could
        // be misinterpreted as serving another, breaking RFC 5869 §3.2.
        var contexts = new[]
        {
            HkdfContexts.OnionLayer.ToArray(),
            HkdfContexts.DirectMessage.ToArray(),
            HkdfContexts.GroupKeyWrap.ToArray(),
        };

        for (int i = 0; i < contexts.Length; i++)
            for (int j = i + 1; j < contexts.Length; j++)
                contexts[i].ShouldNotBe(contexts[j]);
    }

    [Fact]
    public void Pbkdf2Parameters_ArePinned()
    {
        // Reflective check that the public constants on the generator have
        // not drifted. These three values together define every existing
        // user's identity; changing any of them silently is catastrophic.
        CryptoKeyGenerator.SaltSize.ShouldBe(32);

        // Iterations and seed size are private; assert via behavioral proxy:
        // a 32-byte salt + the snapshot passphrase must produce the pinned
        // public keys above. Those tests collectively verify the parameters.
    }

    [Fact]
    public void GenerateSigningKey_AndGenerateKeyPair_DeriveIdenticalSigningHalf()
    {
        // The standalone GenerateSigningKey path must agree with the
        // GenerateKeyPair path on the signing-key bytes — otherwise re-login
        // via the standalone helper would produce a different identity.
        var sut = new CryptoKeyGenerator();
        var salt = SnapshotSalt();

        using var pair = sut.GenerateKeyPair(SnapshotPassphrase, salt);
        using var standalone = sut.GenerateSigningKey(SnapshotPassphrase, salt);

        var standalonePub = standalone.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        standalonePub.ShouldBe(pair.SigningPublicKey);
    }

    [Fact]
    public void GenerateEncryptionKey_AndGenerateKeyPair_DeriveIdenticalEncryptionHalf()
    {
        var sut = new CryptoKeyGenerator();
        var salt = SnapshotSalt();

        using var pair = sut.GenerateKeyPair(SnapshotPassphrase, salt);
        using var standalone = sut.GenerateEncryptionKey(SnapshotPassphrase, salt);

        var standalonePub = standalone.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        standalonePub.ShouldBe(pair.EncryptionPublicKey);
    }
}
