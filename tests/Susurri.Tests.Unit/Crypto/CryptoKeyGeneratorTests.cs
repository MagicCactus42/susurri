using Shouldly;
using Susurri.Modules.IAM.Core.Crypto;
using Xunit;

namespace Susurri.Tests.Unit.Crypto;

public class CryptoKeyGeneratorTests
{
    private readonly CryptoKeyGenerator _sut = new();

    [Fact]
    public void GenerateKeyPair_SamePassphrase_ProducesSameKeys()
    {
        // Arrange
        const string passphrase = "test passphrase for key generation";

        // Act
        using var keyPair1 = _sut.GenerateKeyPair(passphrase);
        using var keyPair2 = _sut.GenerateKeyPair(passphrase);

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
        using var keyPair1 = _sut.GenerateKeyPair(passphrase1);
        using var keyPair2 = _sut.GenerateKeyPair(passphrase2);

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
    public void GenerateSigningKey_SamePassphrase_ProducesSameKey()
    {
        // Arrange
        const string passphrase = "consistent passphrase";

        // Act
        using var key1 = _sut.GenerateSigningKey(passphrase);
        using var key2 = _sut.GenerateSigningKey(passphrase);

        var pubKey1 = key1.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);
        var pubKey2 = key2.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);

        // Assert
        pubKey1.ShouldBe(pubKey2);
    }

    [Fact]
    public void GenerateEncryptionKey_SamePassphrase_ProducesSameKey()
    {
        // Arrange
        const string passphrase = "consistent passphrase";

        // Act
        using var key1 = _sut.GenerateEncryptionKey(passphrase);
        using var key2 = _sut.GenerateEncryptionKey(passphrase);

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
}
