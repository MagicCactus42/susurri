using System.Text;
using NSec.Cryptography;
using Shouldly;
using Susurri.Modules.IAM.Core.Crypto;
using Xunit;

namespace Susurri.Tests.Unit.Crypto;

public class OnionCryptoTests
{
    private readonly OnionCrypto _sut = new();
    private readonly CryptoKeyGenerator _keyGenerator = new();

    [Fact]
    public void Encrypt_Decrypt_RoundTrip_Succeeds()
    {
        // Arrange
        using var recipientKeys = _keyGenerator.GenerateKeyPair("recipient passphrase");
        var plaintext = Encoding.UTF8.GetBytes("Hello, World!");

        // Act
        var envelope = _sut.Encrypt(recipientKeys.EncryptionPublicKey, plaintext);
        var decrypted = _sut.Decrypt(recipientKeys.EncryptionKey, envelope);

        // Assert
        decrypted.ShouldBe(plaintext);
    }

    [Fact]
    public void Encrypt_ProducesValidEnvelope()
    {
        // Arrange
        using var recipientKeys = _keyGenerator.GenerateKeyPair("recipient passphrase");
        var plaintext = Encoding.UTF8.GetBytes("Test message");

        // Act
        var envelope = _sut.Encrypt(recipientKeys.EncryptionPublicKey, plaintext);

        // Assert
        envelope.EphemeralPublicKey.Length.ShouldBe(32); // X25519 public key
        envelope.Nonce.Length.ShouldBe(12); // ChaCha20-Poly1305 nonce
        envelope.Ciphertext.Length.ShouldBe(plaintext.Length + 16); // plaintext + auth tag
    }

    [Fact]
    public void Encrypt_SameMessage_ProducesDifferentCiphertext()
    {
        // Arrange
        using var recipientKeys = _keyGenerator.GenerateKeyPair("recipient passphrase");
        var plaintext = Encoding.UTF8.GetBytes("Test message");

        // Act
        var envelope1 = _sut.Encrypt(recipientKeys.EncryptionPublicKey, plaintext);
        var envelope2 = _sut.Encrypt(recipientKeys.EncryptionPublicKey, plaintext);

        // Assert - different ephemeral keys and nonces mean different ciphertext
        envelope1.EphemeralPublicKey.ShouldNotBe(envelope2.EphemeralPublicKey);
        envelope1.Ciphertext.ShouldNotBe(envelope2.Ciphertext);
    }

    [Fact]
    public void Decrypt_WrongKey_Throws()
    {
        // Arrange
        using var senderKeys = _keyGenerator.GenerateKeyPair("sender passphrase");
        using var recipientKeys = _keyGenerator.GenerateKeyPair("recipient passphrase");
        using var wrongKeys = _keyGenerator.GenerateKeyPair("wrong passphrase");
        var plaintext = Encoding.UTF8.GetBytes("Secret message");

        var envelope = _sut.Encrypt(recipientKeys.EncryptionPublicKey, plaintext);

        // Act & Assert
        Should.Throw<Exception>(() => _sut.Decrypt(wrongKeys.EncryptionKey, envelope));
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_Throws()
    {
        // Arrange
        using var recipientKeys = _keyGenerator.GenerateKeyPair("recipient passphrase");
        var plaintext = Encoding.UTF8.GetBytes("Secret message");

        var envelope = _sut.Encrypt(recipientKeys.EncryptionPublicKey, plaintext);

        // Tamper with ciphertext
        envelope.Ciphertext[0] ^= 0xFF;

        // Act & Assert
        Should.Throw<Exception>(() => _sut.Decrypt(recipientKeys.EncryptionKey, envelope));
    }

    [Fact]
    public void EncryptSymmetric_DecryptSymmetric_RoundTrip_Succeeds()
    {
        // Arrange
        var key = _sut.GenerateSymmetricKey();
        var nonce = _sut.GenerateNonce();
        var plaintext = Encoding.UTF8.GetBytes("Symmetric encryption test");

        // Act
        var ciphertext = _sut.EncryptSymmetric(key, plaintext, nonce);
        var decrypted = _sut.DecryptSymmetric(key, ciphertext, nonce);

        // Assert
        decrypted.ShouldBe(plaintext);
    }

    [Fact]
    public void GenerateSymmetricKey_ProducesCorrectSize()
    {
        // Act
        var key = _sut.GenerateSymmetricKey();

        // Assert
        key.Length.ShouldBe(32); // 256-bit key
    }

    [Fact]
    public void GenerateNonce_ProducesCorrectSize()
    {
        // Act
        var nonce = _sut.GenerateNonce();

        // Assert
        nonce.Length.ShouldBe(12); // ChaCha20-Poly1305 nonce size
    }

    [Fact]
    public void GenerateSymmetricKey_ProducesUniqueKeys()
    {
        // Act
        var key1 = _sut.GenerateSymmetricKey();
        var key2 = _sut.GenerateSymmetricKey();

        // Assert
        key1.ShouldNotBe(key2);
    }

    [Fact]
    public void EncryptedEnvelope_Serialization_RoundTrip()
    {
        // Arrange
        using var recipientKeys = _keyGenerator.GenerateKeyPair("recipient passphrase");
        var plaintext = Encoding.UTF8.GetBytes("Serialization test");

        var envelope = _sut.Encrypt(recipientKeys.EncryptionPublicKey, plaintext);

        // Act
        var bytes = envelope.ToBytes();
        var restored = EncryptedEnvelope.FromBytes(bytes);

        // Assert
        restored.EphemeralPublicKey.ShouldBe(envelope.EphemeralPublicKey);
        restored.Nonce.ShouldBe(envelope.Nonce);
        restored.Ciphertext.ShouldBe(envelope.Ciphertext);

        // Verify we can still decrypt after serialization
        var decrypted = _sut.Decrypt(recipientKeys.EncryptionKey, restored);
        decrypted.ShouldBe(plaintext);
    }

    [Fact]
    public void Encrypt_LargeMessage_Succeeds()
    {
        // Arrange
        using var recipientKeys = _keyGenerator.GenerateKeyPair("recipient passphrase");
        var plaintext = new byte[1024 * 1024]; // 1MB
        new Random(42).NextBytes(plaintext);

        // Act
        var envelope = _sut.Encrypt(recipientKeys.EncryptionPublicKey, plaintext);
        var decrypted = _sut.Decrypt(recipientKeys.EncryptionKey, envelope);

        // Assert
        decrypted.ShouldBe(plaintext);
    }

    [Fact]
    public void Encrypt_EmptyMessage_Succeeds()
    {
        // Arrange
        using var recipientKeys = _keyGenerator.GenerateKeyPair("recipient passphrase");
        var plaintext = Array.Empty<byte>();

        // Act
        var envelope = _sut.Encrypt(recipientKeys.EncryptionPublicKey, plaintext);
        var decrypted = _sut.Decrypt(recipientKeys.EncryptionKey, envelope);

        // Assert
        decrypted.ShouldBe(plaintext);
    }
}
