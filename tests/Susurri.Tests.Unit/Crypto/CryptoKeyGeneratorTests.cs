using Shouldly;
using Susurri.Modules.IAM.Core.Crypto;
using Xunit;

namespace Susurri.Tests.Unit.Crypto;

public class CryptoKeyGeneratorTests
{
    private readonly CryptoKeyGenerator _sut = new();

    // Deterministic salt for tests that need reproducibility
    private static readonly byte[] TestSalt = CreateTestSalt(0x01);

    private static byte[] CreateTestSalt(byte seed)
    {
        var salt = new byte[CryptoKeyGenerator.SaltSize];
        for (int i = 0; i < salt.Length; i++)
            salt[i] = (byte)(seed + i);
        return salt;
    }

    [Fact]
    public void GenerateKeyPair_SamePassphraseAndSalt_ProducesSameKeys()
    {
        // Arrange
        const string passphrase = "test passphrase for key generation";

        // Act
        using var keyPair1 = _sut.GenerateKeyPair(passphrase, TestSalt);
        using var keyPair2 = _sut.GenerateKeyPair(passphrase, TestSalt);

        // Assert
        keyPair1.SigningPublicKey.ShouldBe(keyPair2.SigningPublicKey);
        keyPair1.EncryptionPublicKey.ShouldBe(keyPair2.EncryptionPublicKey);
    }

    [Fact]
    public void GenerateKeyPair_DifferentPassphrases_ProduceDifferentKeys()
    {
        // Arrange
        const string passphrase1 = "first passphrase";
        const string passphrase2 = "second passphrase";

        // Act
        using var keyPair1 = _sut.GenerateKeyPair(passphrase1, TestSalt);
        using var keyPair2 = _sut.GenerateKeyPair(passphrase2, TestSalt);

        // Assert
        keyPair1.SigningPublicKey.ShouldNotBe(keyPair2.SigningPublicKey);
        keyPair1.EncryptionPublicKey.ShouldNotBe(keyPair2.EncryptionPublicKey);
    }

    [Fact]
    public void GenerateKeyPair_DifferentSalts_ProduceDifferentKeys()
    {
        // Arrange
        const string passphrase = "same passphrase";
        var salt1 = CreateTestSalt(0x01);
        var salt2 = CreateTestSalt(0xAA);

        // Act
        using var keyPair1 = _sut.GenerateKeyPair(passphrase, salt1);
        using var keyPair2 = _sut.GenerateKeyPair(passphrase, salt2);

        // Assert — same passphrase with different salts must produce different keys
        keyPair1.SigningPublicKey.ShouldNotBe(keyPair2.SigningPublicKey);
        keyPair1.EncryptionPublicKey.ShouldNotBe(keyPair2.EncryptionPublicKey);
    }

    [Fact]
    public void GenerateKeyPair_WithoutSalt_GeneratesRandomSalt()
    {
        // Act
        using var keyPair = _sut.GenerateKeyPair("test passphrase");

        // Assert
        keyPair.DerivationSalt.ShouldNotBeNull();
        keyPair.DerivationSalt!.Length.ShouldBe(CryptoKeyGenerator.SaltSize);
    }

    [Fact]
    public void GenerateKeyPair_WithoutSalt_ProducesDifferentKeysEachTime()
    {
        // Act — random salt means different keys each call
        using var keyPair1 = _sut.GenerateKeyPair("test passphrase");
        using var keyPair2 = _sut.GenerateKeyPair("test passphrase");

        // Assert
        keyPair1.SigningPublicKey.ShouldNotBe(keyPair2.SigningPublicKey);
        keyPair1.EncryptionPublicKey.ShouldNotBe(keyPair2.EncryptionPublicKey);
    }

    [Fact]
    public void GenerateKeyPair_ProducesCorrectKeySizes()
    {
        // Arrange
        const string passphrase = "test passphrase";

        // Act
        using var keyPair = _sut.GenerateKeyPair(passphrase);

        // Assert
        keyPair.SigningPublicKey.Length.ShouldBe(32); // Ed25519 public key is 32 bytes
        keyPair.EncryptionPublicKey.Length.ShouldBe(32); // X25519 public key is 32 bytes
    }

    [Fact]
    public void GenerateKeyPair_SigningAndEncryptionKeysAreDifferent()
    {
        // Arrange
        const string passphrase = "test passphrase";

        // Act
        using var keyPair = _sut.GenerateKeyPair(passphrase);

        // Assert
        keyPair.SigningPublicKey.ShouldNotBe(keyPair.EncryptionPublicKey);
    }

    [Fact]
    public void GenerateSigningKey_SamePassphraseAndSalt_ProducesSameKey()
    {
        // Arrange
        const string passphrase = "consistent passphrase";

        // Act
        using var key1 = _sut.GenerateSigningKey(passphrase, TestSalt);
        using var key2 = _sut.GenerateSigningKey(passphrase, TestSalt);

        var pubKey1 = key1.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);
        var pubKey2 = key2.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);

        // Assert
        pubKey1.ShouldBe(pubKey2);
    }

    [Fact]
    public void GenerateEncryptionKey_SamePassphraseAndSalt_ProducesSameKey()
    {
        // Arrange
        const string passphrase = "consistent passphrase";

        // Act
        using var key1 = _sut.GenerateEncryptionKey(passphrase, TestSalt);
        using var key2 = _sut.GenerateEncryptionKey(passphrase, TestSalt);

        var pubKey1 = key1.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);
        var pubKey2 = key2.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);

        // Assert
        pubKey1.ShouldBe(pubKey2);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("a very long passphrase that contains many words and characters")]
    [InlineData("special !@#$%^&*() characters")]
    [InlineData("unicode パスワード 密码")]
    public void GenerateKeyPair_VariousPassphrases_ProducesValidKeys(string passphrase)
    {
        // Act
        using var keyPair = _sut.GenerateKeyPair(passphrase);

        // Assert
        keyPair.SigningPublicKey.ShouldNotBeNull();
        keyPair.EncryptionPublicKey.ShouldNotBeNull();
        keyPair.SigningPublicKey.Length.ShouldBe(32);
        keyPair.EncryptionPublicKey.Length.ShouldBe(32);
    }

    [Fact]
    public void GeneratePassphrase_DefaultWordCount_Returns8Words()
    {
        // Act
        var passphrase = _sut.GeneratePassphrase();

        // Assert
        var words = passphrase.Split(' ');
        words.Length.ShouldBe(8);
    }

    [Fact]
    public void GeneratePassphrase_CustomWordCount_ReturnsCorrectCount()
    {
        // Act
        var passphrase = _sut.GeneratePassphrase(12);

        // Assert
        var words = passphrase.Split(' ');
        words.Length.ShouldBe(12);
    }

    [Fact]
    public void GeneratePassphrase_TooFewWords_Throws()
    {
        Should.Throw<ArgumentException>(() => _sut.GeneratePassphrase(5));
    }

    [Fact]
    public void GeneratePassphrase_TooManyWords_Throws()
    {
        Should.Throw<ArgumentException>(() => _sut.GeneratePassphrase(21));
    }

    [Fact]
    public void GeneratePassphrase_ProducesUniquePassphrases()
    {
        // Act
        var p1 = _sut.GeneratePassphrase();
        var p2 = _sut.GeneratePassphrase();

        // Assert
        p1.ShouldNotBe(p2);
    }

    [Fact]
    public void GenerateKeyPairLegacy_SamePassphrase_ProducesSameKeys()
    {
        // Legacy BIP39 derivation is deterministic (no salt)
        using var keyPair1 = CryptoKeyGenerator.GenerateKeyPairLegacy("test passphrase");
        using var keyPair2 = CryptoKeyGenerator.GenerateKeyPairLegacy("test passphrase");

        keyPair1.SigningPublicKey.ShouldBe(keyPair2.SigningPublicKey);
        keyPair1.EncryptionPublicKey.ShouldBe(keyPair2.EncryptionPublicKey);
        keyPair1.DerivationSalt.ShouldBeNull();
    }

    [Fact]
    public void GenerateKeyPairLegacy_ProducesDifferentKeysFromNewDerivation()
    {
        // Legacy and new derivation from same passphrase should differ
        using var legacy = CryptoKeyGenerator.GenerateKeyPairLegacy("test passphrase");
        using var modern = _sut.GenerateKeyPair("test passphrase", TestSalt);

        legacy.SigningPublicKey.ShouldNotBe(modern.SigningPublicKey);
        legacy.EncryptionPublicKey.ShouldNotBe(modern.EncryptionPublicKey);
    }

    [Fact]
    public void GenerateKeyPair_InvalidSalt_Throws()
    {
        Should.Throw<ArgumentException>(() => _sut.GenerateKeyPair("passphrase", new byte[16]));
        Should.Throw<ArgumentNullException>(() => _sut.GenerateKeyPair("passphrase", null!));
    }
}
